#!/usr/bin/env bash
#
# Runs one emergency account-recovery operation through a transient, least-privilege Cloud Run job.
# The normal provisioning job is deleted immediately after every deployment and must not be retained
# as a standing database-administration shell. This helper uses a separate service account that can
# read only the emergency-reset login secret and holds no schema or account-provisioning credential.
#
# Usage: ./scripts/emergency-reset-cloudrun-postgresql.sh <gcp-project-id> <password|two-factor> <username> [region]
set -euo pipefail
umask 077

project="${1:?Usage: $0 <gcp-project-id> <password|two-factor> <username> [region]}"
mode="${2:?Usage: $0 <gcp-project-id> <password|two-factor> <username> [region]}"
username="${3:?Usage: $0 <gcp-project-id> <password|two-factor> <username> [region]}"
region="${4:-europe-west1}"

case "$mode" in
password | two-factor) ;;
*)
	echo "ERROR: mode must be 'password' or 'two-factor'." >&2
	exit 2
	;;
esac

if [[ $username == *','* ]]; then
	echo "ERROR: username cannot contain a comma in the Cloud Run job argument boundary." >&2
	exit 2
fi

readonly repository=cloud-run-source-deploy
readonly provision_image_path="$region-docker.pkg.dev/$project/$repository/jobtrack-provision"
readonly recovery_job=jobtrack-emergency-reset
readonly sql_instance=jobtrack-pg
readonly sql_database=jobtrack
readonly emergency_service_account="jobtrack-emergency-reset@$project.iam.gserviceaccount.com"
readonly instance_connection_name="$project:$region:$sql_instance"
readonly db_host="/cloudsql/$instance_connection_name"
readonly emergency_access_duration_minutes=15

if emergency_access_expiry="$(date -u -v+"${emergency_access_duration_minutes}"M '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null)"; then
	:
else
	emergency_access_expiry="$(date -u -d "+$emergency_access_duration_minutes minutes" '+%Y-%m-%dT%H:%M:%SZ')"
fi
readonly emergency_access_expiry
readonly emergency_access_condition="title=jobtrack-emergency-$(date -u '+%Y%m%d%H%M%S'),description=Temporary JobTrack emergency recovery access,expression=request.time < timestamp(\"$emergency_access_expiry\")"

cleanup() {
	gcloud run jobs delete "$recovery_job" \
		--project="$project" --region="$region" --quiet >/dev/null 2>&1 || true
	gcloud secrets remove-iam-policy-binding jobtrack-role-password-emergency-reset \
		--project="$project" \
		--member="serviceAccount:$emergency_service_account" \
		--role=roles/secretmanager.secretAccessor --condition="$emergency_access_condition" --quiet >/dev/null 2>&1 || true
	gcloud projects remove-iam-policy-binding "$project" \
		--member="serviceAccount:$emergency_service_account" \
		--role=roles/cloudsql.client --condition="$emergency_access_condition" --quiet >/dev/null 2>&1 || true
}

trap cleanup EXIT

provision_digest="$(gcloud artifacts docker images list "$provision_image_path" \
	--project="$project" --include-tags --filter='tags:*' --sort-by='~UPDATE_TIME' --limit=1 \
	--format='value(DIGEST)')"
if [[ -z $provision_digest ]]; then
	echo "ERROR: no tagged provisioning image exists under $provision_image_path." >&2
	exit 1
fi

provision_image_by_digest="$provision_image_path@$provision_digest"
emergency_secret_version="$(gcloud secrets versions list jobtrack-role-password-emergency-reset \
	--project="$project" --filter='state=ENABLED' --sort-by='~createTime' --limit=1 --format='value(name)')"
if [[ -z $emergency_secret_version ]]; then
	echo "ERROR: jobtrack-role-password-emergency-reset has no enabled version." >&2
	exit 1
fi

echo "==> granting temporary emergency-recovery access"
# Reconcile unconditional grants left by versions predating expiring IAM conditions.
gcloud projects remove-iam-policy-binding "$project" \
	--member="serviceAccount:$emergency_service_account" \
	--role=roles/cloudsql.client --condition=None --quiet >/dev/null 2>&1 || true
gcloud secrets remove-iam-policy-binding jobtrack-role-password-emergency-reset \
	--project="$project" \
	--member="serviceAccount:$emergency_service_account" \
	--role=roles/secretmanager.secretAccessor --condition=None --quiet >/dev/null 2>&1 || true
gcloud projects add-iam-policy-binding "$project" \
	--member="serviceAccount:$emergency_service_account" \
	--role=roles/cloudsql.client --condition="$emergency_access_condition" --quiet >/dev/null
gcloud secrets add-iam-policy-binding jobtrack-role-password-emergency-reset \
	--project="$project" \
	--member="serviceAccount:$emergency_service_account" \
	--role=roles/secretmanager.secretAccessor --condition="$emergency_access_condition" --quiet >/dev/null

echo "==> deploying transient emergency-recovery job"
gcloud run jobs deploy "$recovery_job" \
	--project="$project" \
	--region="$region" \
	--image="$provision_image_by_digest" \
	--service-account="$emergency_service_account" \
	--set-cloudsql-instances="$instance_connection_name" \
	--binary-authorization=default \
	--max-retries=0 \
	--task-timeout=5m \
	--command=/app/sql/emergency-reset.sh \
	--args="$mode,$username" \
	--set-env-vars="^@^JOBTRACK_DB_HOST=$db_host@JOBTRACK_DB_NAME=$sql_database" \
	--set-secrets="JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET=jobtrack-role-password-emergency-reset:$emergency_secret_version" \
	--execute-now --wait --quiet

echo "==> emergency recovery complete; deleting the transient job"
