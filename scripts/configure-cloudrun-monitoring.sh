#!/usr/bin/env bash
#
# Reconcile the live PostgreSQL deployment's audit and alerting baseline. Product behaviour does not
# drive deployment-harness configuration, so this script is verified by bash -n, gcloud read-back,
# and the live checks documented in docs/operations/monitoring-and-alerts.md.
#
# Usage: ./scripts/configure-cloudrun-monitoring.sh <gcp-project-id> [region]
#
# JOBTRACK_MONITORING_NOTIFICATION_CHANNEL must be the full resource name of an enabled Cloud
# Monitoring notification channel that is not explicitly unverified. A policy with no delivery
# destination looks configured but cannot wake an operator, so this script fails closed instead of
# creating one.
set -euo pipefail
umask 077

project="${1:?Usage: $0 <gcp-project-id> [region]}"
region="${2:-europe-west1}"
service=jobtrack-web-pg
sql_instance=jobtrack-pg
notification_channel="${JOBTRACK_MONITORING_NOTIFICATION_CHANNEL:?Set JOBTRACK_MONITORING_NOTIFICATION_CHANNEL to a verified Cloud Monitoring notification-channel resource name.}"
managed_label=jobtrack_deploy
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/jobtrack-monitoring.XXXXXX")"
project_policy_before="$temporary_directory/project-policy-before.json"
project_policy_after="$temporary_directory/project-policy-after.json"
policy_file="$temporary_directory/alert-policy.json"
curl_config="$temporary_directory/curl.conf"

cleanup() {
	rm -rf "$temporary_directory"
}
trap cleanup EXIT

for required_command in curl gcloud jq; do
	if ! command -v "$required_command" >/dev/null 2>&1; then
		echo "ERROR: required command '$required_command' is not installed." >&2
		exit 1
	fi
done

export CLOUDSDK_CORE_PROJECT="$project"

# Validate the channel through the stable REST API because notification-channel commands are still
# a separately installed beta gcloud component. Put the OAuth token in a mode-0600 curl config file,
# never argv, shell trace, or deploy output.
case $notification_channel in
	projects/*/notificationChannels/*) ;;
	*)
		echo "ERROR: JOBTRACK_MONITORING_NOTIFICATION_CHANNEL must be a full projects/.../notificationChannels/... resource name." >&2
		exit 2
		;;
esac
access_token="$(gcloud auth print-access-token)"
printf 'header = "Authorization: Bearer %s"\nsilent\nshow-error\nfail\n' "$access_token" >"$curl_config"
unset access_token
channel_json="$(curl --config "$curl_config" "https://monitoring.googleapis.com/v3/$notification_channel")"
if [[ $(jq -r '.enabled // false' <<<"$channel_json") != true ]]; then
	echo "ERROR: monitoring notification channel $notification_channel is not enabled." >&2
	exit 1
fi
verification_status="$(jq -r '.verificationStatus // "VERIFICATION_STATUS_UNSPECIFIED"' <<<"$channel_json")"
case $verification_status in
	VERIFIED | VERIFICATION_STATUS_UNSPECIFIED) ;;
	UNVERIFIED)
		echo "ERROR: monitoring notification channel $notification_channel requires verification." >&2
		exit 1
		;;
	*)
		echo "ERROR: monitoring notification channel $notification_channel returned unknown verification status '$verification_status'." >&2
		exit 1
		;;
esac

echo "==> enabling targeted Data Access audit logs"
gcloud projects get-iam-policy "$project" --format=json >"$project_policy_before"
jq '
	def merge_audit_config($required):
		if any(.service == $required.service) then
			map(if .service == $required.service then
				.auditLogConfigs = ((.auditLogConfigs // []) as $existing
					| [$existing[]
						| if .logType as $existing_type
							| any($required.auditLogConfigs[]; .logType == $existing_type)
						then {logType: .logType}
						else . end]
					+ [$required.auditLogConfigs[]
						| select(.logType as $required_type
							| all($existing[]; .logType != $required_type))])
			else . end)
		else . + [$required] end;
	.auditConfigs = ((.auditConfigs // [])
		| merge_audit_config({
			service: "secretmanager.googleapis.com",
			auditLogConfigs: [{logType: "ADMIN_READ"}, {logType: "DATA_READ"}]
		})
		| merge_audit_config({
			service: "iam.googleapis.com",
			auditLogConfigs: [{logType: "ADMIN_READ"}, {logType: "DATA_READ"}]
		})
		| merge_audit_config({
			service: "cloudkms.googleapis.com",
			auditLogConfigs: [{logType: "ADMIN_READ"}, {logType: "DATA_READ"}]
		}))
' "$project_policy_before" >"$project_policy_after"

project_policy_before_canonical="$(jq -S -c . "$project_policy_before")"
project_policy_after_canonical="$(jq -S -c . "$project_policy_after")"
if [[ $project_policy_before_canonical != "$project_policy_after_canonical" ]]; then
	gcloud projects set-iam-policy "$project" "$project_policy_after" --quiet >/dev/null
fi

ensure_log_metric() {
	local name=$1 description=$2 filter=$3
	if gcloud logging metrics describe "$name" --project="$project" >/dev/null 2>&1; then
		gcloud logging metrics update "$name" --project="$project" \
			--description="$description" --log-filter="$filter" --quiet >/dev/null
	else
		gcloud logging metrics create "$name" --project="$project" \
			--description="$description" --log-filter="$filter" --quiet >/dev/null
	fi
}

echo "==> reconciling log-based metrics"
ensure_log_metric jobtrack_cloud_run_instance_starts \
	"Cloud Run instance-start events for the live JobTrack service." \
	"resource.type=\"cloud_run_revision\" AND resource.labels.service_name=\"$service\" AND (textPayload:\"Starting new instance\" OR jsonPayload.message:\"Starting new instance\")"
ensure_log_metric jobtrack_cloud_sql_backup_failures \
	"Failed automated backup attempts for the live JobTrack Cloud SQL instance." \
	"resource.type=\"cloudsql_database\" AND protoPayload.serviceName=\"cloudsql.googleapis.com\" AND protoPayload.methodName=\"cloudsql.instances.automatedBackup\" AND protoPayload.resourceName=\"projects/$project/instances/$sql_instance\" AND protoPayload.metadata.windowStatus!=\"STATUS_SUCCEEDED\""

write_policy() {
	local display_name=$1 documentation=$2 condition=$3
	jq -n \
		--arg displayName "$display_name" \
		--arg documentation "$documentation" \
		--arg channel "$notification_channel" \
		--arg managedBy "$managed_label" \
		--argjson condition "$condition" '
		{
			displayName: $displayName,
			combiner: "OR",
			enabled: true,
			documentation: {content: $documentation, mimeType: "text/markdown"},
			notificationChannels: [$channel],
			userLabels: {managed_by: $managedBy},
			conditions: [$condition],
			# OPENED only. A closure notification reads "<condition> is below threshold of <N> with a
			# value of <M>", which is easily mistaken for a firing alert; the recovery carries no
			# action for an operator. Incidents still auto-close after a week.
			alertStrategy: {
				autoClose: "604800s",
				notificationPrompts: ["OPENED"]
			}
		}' >"$policy_file"
}

ensure_policy() {
	local display_name=$1 documentation=$2 condition=$3 existing_policy policy_count
	write_policy "$display_name" "$documentation" "$condition"
	existing_policy="$(gcloud monitoring policies list --project="$project" \
		--filter="displayName=\"$display_name\"" --format='value(name)')"
	policy_count="$(wc -w <<<"$existing_policy" | tr -d ' ')"
	if [[ $policy_count -gt 1 ]]; then
		echo "ERROR: more than one alert policy is named '$display_name'; reconcile duplicates manually." >&2
		exit 1
	fi
	if [[ -n $existing_policy ]]; then
		gcloud monitoring policies update "$existing_policy" --project="$project" \
			--policy-from-file="$policy_file" --quiet >/dev/null
	else
		gcloud monitoring policies create --project="$project" \
			--policy-from-file="$policy_file" --quiet >/dev/null
	fi
}

cloud_run_resource_filter="resource.type=\"cloud_run_revision\" AND resource.label.\"service_name\"=\"$service\" AND resource.label.\"location\"=\"$region\""
cloud_sql_resource_filter="resource.type=\"cloudsql_database\" AND resource.label.\"database_id\"=\"$project:$sql_instance\""

echo "==> reconciling Cloud Monitoring alert policies"
ensure_policy "JobTrack Cloud Run sustained 5xx responses" \
	"More than 15 container-served 5xx responses in five minutes. Check the serving revision, dependency health, and recent changes." \
	"$(jq -cn --arg filter "metric.type=\"run.googleapis.com/request_count\" AND metric.label.\"response_code_class\"=\"5xx\" AND $cloud_run_resource_filter" '{
		displayName: "5xx response rate exceeds 0.05/s",
		conditionThreshold: {
			filter: $filter,
			comparison: "COMPARISON_GT",
			thresholdValue: 0.05,
			duration: "300s",
			aggregations: [{alignmentPeriod: "300s", perSeriesAligner: "ALIGN_RATE", crossSeriesReducer: "REDUCE_SUM", groupByFields: ["resource.label.service_name"]}],
			trigger: {count: 1}
		}
	}')"

ensure_policy "JobTrack Cloud Run p95 latency" \
	"Cloud Run p95 request latency exceeded two seconds for five minutes. Check database pressure, pool waits, and the serving revision." \
	"$(jq -cn --arg filter "metric.type=\"run.googleapis.com/request_latencies\" AND $cloud_run_resource_filter" '{
		displayName: "p95 latency exceeds 2000 ms",
		conditionThreshold: {
			filter: $filter,
			comparison: "COMPARISON_GT",
			thresholdValue: 2000,
			duration: "300s",
			aggregations: [{alignmentPeriod: "300s", perSeriesAligner: "ALIGN_PERCENTILE_95", crossSeriesReducer: "REDUCE_MAX", groupByFields: ["resource.label.service_name"]}],
			trigger: {count: 1}
		}
	}')"

ensure_policy "JobTrack Cloud Run restart loop" \
	"More than ten instance starts in five minutes. Distinguish expected scale-out from crash/startup churn before changing capacity." \
	"$(jq -cn --arg filter "metric.type=\"logging.googleapis.com/user/jobtrack_cloud_run_instance_starts\" AND $cloud_run_resource_filter" '{
		displayName: "instance starts exceed ten per five minutes",
		conditionThreshold: {
			filter: $filter,
			comparison: "COMPARISON_GT",
			thresholdValue: 10,
			duration: "0s",
			aggregations: [{alignmentPeriod: "300s", perSeriesAligner: "ALIGN_DELTA", crossSeriesReducer: "REDUCE_SUM", groupByFields: ["resource.label.service_name"]}],
			trigger: {count: 1}
		}
	}')"

ensure_policy "JobTrack Cloud SQL disk saturation" \
	"Cloud SQL disk utilization exceeded 80% for ten minutes. Confirm auto-resize health and investigate abnormal growth." \
	"$(jq -cn --arg filter "metric.type=\"cloudsql.googleapis.com/database/disk/utilization\" AND $cloud_sql_resource_filter" '{
		displayName: "disk utilization exceeds 80%",
		conditionThreshold: {
			filter: $filter,
			comparison: "COMPARISON_GT",
			thresholdValue: 0.8,
			duration: "600s",
			aggregations: [{alignmentPeriod: "300s", perSeriesAligner: "ALIGN_MAX"}],
			trigger: {count: 1}
		}
	}')"

ensure_policy "JobTrack Cloud SQL connection saturation" \
	"PostgreSQL backends exceeded 80 of the verified 100-connection ceiling for five minutes. Check pool usage before increasing any host limit." \
	"$(jq -cn --arg filter "metric.type=\"cloudsql.googleapis.com/database/postgresql/num_backends\" AND $cloud_sql_resource_filter" '{
		displayName: "PostgreSQL backends exceed 80",
		conditionThreshold: {
			filter: $filter,
			comparison: "COMPARISON_GT",
			thresholdValue: 80,
			duration: "300s",
			aggregations: [{alignmentPeriod: "300s", perSeriesAligner: "ALIGN_MAX", crossSeriesReducer: "REDUCE_SUM", groupByFields: ["resource.label.database_id"]}],
			trigger: {count: 1}
		}
	}')"

ensure_policy "JobTrack Cloud SQL automated backup failure" \
	"An automated Cloud SQL backup attempt failed. Inspect the backup run immediately and preserve the last known-good recovery point." \
	"$(jq -cn --arg filter "metric.type=\"logging.googleapis.com/user/jobtrack_cloud_sql_backup_failures\" AND $cloud_sql_resource_filter" '{
		displayName: "automated backup failure count exceeds zero",
		conditionThreshold: {
			filter: $filter,
			comparison: "COMPARISON_GT",
			thresholdValue: 0,
			duration: "0s",
			aggregations: [{alignmentPeriod: "300s", perSeriesAligner: "ALIGN_DELTA", crossSeriesReducer: "REDUCE_SUM", groupByFields: ["resource.label.database_id"]}],
			trigger: {count: 1}
		}
	}')"

echo "==> monitoring baseline reconciled with verified notification delivery"
