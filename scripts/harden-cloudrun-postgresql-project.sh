#!/usr/bin/env bash
#
# Remove default-project residue that the dedicated JobTrack Cloud Run + Cloud SQL topology never
# uses. This project is intentionally single-purpose (docs/operations/postgresql-cloud-run-deployment.md
# §"Identity and least privilege"); the script fails rather than deleting a non-empty bucket or a VPC
# with a compute workload attached.
#
# Usage: ./scripts/harden-cloudrun-postgresql-project.sh <gcp-project-id>
set -euo pipefail

project="${1:?Usage: $0 <gcp-project-id>}"
for required_command in gcloud grep jq; do
	if ! command -v "$required_command" >/dev/null 2>&1; then
		echo "ERROR: required command '$required_command' is not installed." >&2
		exit 1
	fi
done

export CLOUDSDK_CORE_PROJECT="$project"
project_number="$(gcloud projects describe "$project" --format='value(projectNumber)')"
default_compute_service_account="$project_number-compute@developer.gserviceaccount.com"
legacy_cloud_build_service_account="$project_number@cloudbuild.gserviceaccount.com"

if gcloud services list --enabled --project="$project" \
	--filter='config.name=compute.googleapis.com' --format='value(config.name)' | grep -qx compute.googleapis.com; then
	if [[ -n $(gcloud compute instances list --project="$project" --format='value(name)') ]]; then
		echo "ERROR: Compute Engine instances exist; refusing to remove the default VPC." >&2
		exit 1
	fi
	if [[ -n $(gcloud compute forwarding-rules list --project="$project" --format='value(name)') ]]; then
		echo "ERROR: forwarding rules exist; refusing to remove the default VPC." >&2
		exit 1
	fi
	if [[ -n $(gcloud compute networks peerings list --network=default --project="$project" --format='value(name)' 2>/dev/null) ]]; then
		echo "ERROR: the default VPC has peerings; refusing to remove it." >&2
		exit 1
	fi

	if gcloud compute networks describe default --project="$project" >/dev/null 2>&1; then
		echo "==> removing unused internet-facing default-network firewall rules"
		while read -r firewall_rule; do
			[[ -n $firewall_rule ]] || continue
			gcloud compute firewall-rules delete "$firewall_rule" --project="$project" --quiet >/dev/null
		done < <(gcloud compute firewall-rules list --project="$project" \
			--filter='network:default' --format='value(name)')

		echo "==> removing the unused default VPC"
		gcloud compute networks delete default --project="$project" --quiet >/dev/null
	fi
fi

echo "==> disabling the unused default compute service account"
if gcloud iam service-accounts describe "$default_compute_service_account" \
	--project="$project" >/dev/null 2>&1; then
	gcloud iam service-accounts disable "$default_compute_service_account" \
		--project="$project" --quiet >/dev/null
	default_compute_disabled="$(gcloud iam service-accounts describe "$default_compute_service_account" \
		--project="$project" --format='value(disabled)')"
	if [[ $default_compute_disabled != True ]]; then
		echo "ERROR: default compute service account remains enabled." >&2
		exit 1
	fi
fi

# Local buildx supplies every deployed image. There are no Cloud Build executions in this topology,
# so its legacy builder identity must not retain project-wide write permissions.
echo "==> removing obsolete Cloud Build builder authority"
gcloud projects remove-iam-policy-binding "$project" \
	--member="serviceAccount:$legacy_cloud_build_service_account" \
	--role=roles/cloudbuild.builds.builder --condition=None --quiet >/dev/null 2>&1 || true
if gcloud projects get-iam-policy "$project" --format=json |
	jq -e --arg member "serviceAccount:$legacy_cloud_build_service_account" '
		any(.bindings[];
			.role == "roles/cloudbuild.builds.builder"
			and any(.members[]?; . == $member))' >/dev/null; then
	echo "ERROR: legacy Cloud Build service account retains roles/cloudbuild.builds.builder." >&2
	exit 1
fi

delete_empty_bucket() {
	local bucket=$1
	if ! gcloud storage buckets describe "gs://$bucket" --project="$project" >/dev/null 2>&1; then
		return 0
	fi
	if [[ -n $(gcloud storage ls --recursive "gs://$bucket/**" 2>/dev/null) ]]; then
		echo "ERROR: gs://$bucket is not empty; refusing to delete it." >&2
		exit 1
	fi
	echo "==> deleting empty obsolete bucket gs://$bucket"
	gcloud storage buckets delete "gs://$bucket" --project="$project" --quiet >/dev/null
}

delete_empty_bucket "${project}_cloudbuild"
delete_empty_bucket "run-sources-${project}-europe-west1"
delete_empty_bucket "run-sources-${project}-europe-west2"

# Refuse dependency cascades: if another enabled service still requires Cloud Build, this command
# fails and the operator investigates instead of using --force to disable an unknown dependency.
if gcloud services list --enabled --project="$project" \
	--filter='config.name=cloudbuild.googleapis.com' --format='value(config.name)' | grep -qx cloudbuild.googleapis.com; then
	echo "==> disabling the unused Cloud Build API"
	gcloud services disable cloudbuild.googleapis.com --project="$project" --quiet >/dev/null
fi
if gcloud services list --enabled --project="$project" \
	--filter='config.name=cloudbuild.googleapis.com' --format='value(config.name)' | grep -qx cloudbuild.googleapis.com; then
	echo "ERROR: Cloud Build API remains enabled." >&2
	exit 1
fi

echo "==> dedicated-project residue removed"
