#!/usr/bin/env bash
#
# One-time, project-wide Binary Authorization setup for the persistent PostgreSQL deployment.
# The deployment script subsequently signs each scanned image digest with this KMS key and Cloud Run
# refuses every image that lacks a matching attestation.
#
# Usage: ./scripts/configure-cloudrun-binary-authorization.sh <gcp-project-id> [region]
set -euo pipefail
umask 077

project="${1:?Usage: $0 <gcp-project-id> [region]}"
region="${2:-europe-west1}"
attestor="jobtrack-release"
note="jobtrack-release"
keyring="jobtrack-release"
key="image-attestation"
key_version="1"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/jobtrack-binauthz.XXXXXX")"
authentication_config="$temporary_directory/curl-auth.conf"
note_payload="$temporary_directory/note.json"
policy_file="$temporary_directory/policy.yaml"

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

echo "==> enabling Binary Authorization, Artifact Analysis, and Cloud KMS"
gcloud services enable \
	binaryauthorization.googleapis.com \
	containeranalysis.googleapis.com \
	cloudkms.googleapis.com \
	--project="$project" --quiet

# Keep the OAuth bearer token in a mode-0600 curl configuration file, not a process argument visible
# to other local users through the process table.
access_token="$(gcloud auth print-access-token)"
printf 'header = "Authorization: Bearer %s"\nheader = "x-goog-user-project: %s"\n' \
	"$access_token" "$project" >"$authentication_config"
unset access_token

authenticated_curl() {
	curl --fail --silent --show-error --config "$authentication_config" "$@"
}

if ! gcloud kms keyrings describe "$keyring" --location="$region" --project="$project" >/dev/null 2>&1; then
	echo "==> creating the release-attestation KMS key ring"
	gcloud kms keyrings create "$keyring" --location="$region" --project="$project" --quiet
fi

if ! gcloud kms keys describe "$key" \
	--keyring="$keyring" --location="$region" --project="$project" >/dev/null 2>&1; then
	echo "==> creating the release-attestation asymmetric signing key"
	gcloud kms keys create "$key" \
		--keyring="$keyring" --location="$region" --project="$project" \
		--purpose=asymmetric-signing \
		--default-algorithm=ec-sign-p256-sha256 \
		--protection-level=software \
		--quiet
fi

note_url="https://containeranalysis.googleapis.com/v1/projects/$project/notes/$note"
if ! authenticated_curl "$note_url" >/dev/null 2>&1; then
	echo "==> creating the Artifact Analysis attestation note"
	jq -n \
		--arg name "projects/$project/notes/$note" \
		--arg description "JobTrack release passed the deployment vulnerability gate" \
		'{name: $name, attestation: {hint: {humanReadableName: $description}}}' >"$note_payload"
	authenticated_curl \
		-X POST \
		-H 'Content-Type: application/json' \
		--data-binary "@$note_payload" \
		"https://containeranalysis.googleapis.com/v1/projects/$project/notes/?noteId=$note" >/dev/null
fi

if ! gcloud container binauthz attestors describe "$attestor" --project="$project" >/dev/null 2>&1; then
	echo "==> creating the Binary Authorization attestor"
	gcloud container binauthz attestors create "$attestor" \
		--project="$project" \
		--attestation-authority-note="$note" \
		--attestation-authority-note-project="$project" \
		--description="JobTrack release vulnerability gate" \
		--quiet
fi

key_resource="projects/$project/locations/$region/keyRings/$keyring/cryptoKeys/$key/cryptoKeyVersions/$key_version"
if ! gcloud container binauthz attestors describe "$attestor" --project="$project" --format=json |
	jq -e --arg key_resource "$key_resource" \
		'(.userOwnedGrafeasNote.publicKeys // [])
			| any(.pkixPublicKey.publicKeyPem and (.id | contains($key_resource)))' \
		>/dev/null; then
	echo "==> adding the KMS verification key to the attestor"
	gcloud container binauthz attestors public-keys add \
		--project="$project" \
		--attestor="$attestor" \
		--keyversion-project="$project" \
		--keyversion-location="$region" \
		--keyversion-keyring="$keyring" \
		--keyversion-key="$key" \
		--keyversion="$key_version" \
		--quiet
fi

project_number="$(gcloud projects describe "$project" --format='value(projectNumber)')"
binary_authorization_service_account="service-$project_number@gcp-sa-binaryauthorization.iam.gserviceaccount.com"
delegation_service_account="$(gcloud container binauthz attestors describe "$attestor" \
	--project="$project" --format='value(userOwnedGrafeasNote.delegationServiceAccountEmail)')"

echo "==> granting the Binary Authorization service identities verification-only access"
gcloud container binauthz attestors add-iam-policy-binding "$attestor" \
	--project="$project" \
	--member="serviceAccount:$binary_authorization_service_account" \
	--role=roles/binaryauthorization.attestorsVerifier \
	--condition=None --quiet >/dev/null

# The attestor's fixed delegation identity needs occurrence-viewer access to its own note. Artifact
# Analysis exposes note IAM only through REST, so merge this one member without replacing any policy.
note_iam_policy="$temporary_directory/note-iam-policy.json"
note_iam_request="$temporary_directory/note-iam-request.json"
authenticated_curl \
	-X POST \
	-H 'Content-Type: application/json' \
	--data '{}' \
	"$note_url:getIamPolicy" >"$note_iam_policy"
jq --arg member "serviceAccount:$delegation_service_account" '
	if any(.bindings[]?; .role == "roles/containeranalysis.notes.occurrences.viewer" and any(.members[]?; . == $member)) then
		.
	elif any(.bindings[]?; .role == "roles/containeranalysis.notes.occurrences.viewer") then
		.bindings |= map(
			if .role == "roles/containeranalysis.notes.occurrences.viewer" then .members += [$member] else . end)
	else
		.bindings += [{role: "roles/containeranalysis.notes.occurrences.viewer", members: [$member]}]
	end
	| {policy: .}' "$note_iam_policy" >"$note_iam_request"
authenticated_curl \
	-X POST \
	-H 'Content-Type: application/json' \
	--data-binary "@$note_iam_request" \
	"$note_url:setIamPolicy" >/dev/null

# This project-wide policy is intentionally fail-closed. The setup command is separate from deploy
# because replacing a Binary Authorization policy is a security-administration action, not a routine
# application release side effect.
printf '%s\n' \
	'globalPolicyEvaluationMode: ENABLE' \
	'defaultAdmissionRule:' \
	'  evaluationMode: REQUIRE_ATTESTATION' \
	'  enforcementMode: ENFORCED_BLOCK_AND_AUDIT_LOG' \
	'  requireAttestationsBy:' \
	"  - projects/$project/attestors/$attestor" \
	"name: projects/$project/policy" >"$policy_file"

existing_policy="$(gcloud container binauthz policy export --project="$project" --format=json)"
if ! jq -e --arg attestor "projects/$project/attestors/$attestor" '
	def has_no_scoped_rules:
		([.clusterAdmissionRules, .kubernetesNamespaceAdmissionRules,
		  .kubernetesServiceAccountAdmissionRules, .istioServiceIdentityAdmissionRules]
		 | map((. // {}) | length) | add) == 0
		and ((.admissionWhitelistPatterns // []) | length) == 0;
	has_no_scoped_rules and (
		.defaultAdmissionRule.evaluationMode == "ALWAYS_ALLOW" or (
			.defaultAdmissionRule.evaluationMode == "REQUIRE_ATTESTATION" and
			.defaultAdmissionRule.enforcementMode == "ENFORCED_BLOCK_AND_AUDIT_LOG" and
			.defaultAdmissionRule.requireAttestationsBy == [$attestor]))' \
	<<<"$existing_policy" >/dev/null; then
	echo "ERROR: the existing policy has custom admission rules; refusing to replace customized security policy." >&2
	echo "Merge projects/$project/attestors/$attestor into that policy deliberately, then rerun this command." >&2
	exit 1
fi

echo "==> importing the fail-closed Binary Authorization policy"
gcloud container binauthz policy import "$policy_file" --project="$project" --quiet

# Defence in depth, and deliberately non-fatal. What gates a JobTrack release is the fail-closed policy
# imported above, evaluated because every deployment script passes --binary-authorization=default. This
# organization policy closes a different hole: it stops some *other* deployment in the project opting
# out of Binary Authorization entirely. Setting it needs roles/orgpolicy.policyAdmin, which a project
# owner does not hold by default, so failing here would report a correctly configured project as broken.
echo "==> requiring Binary Authorization on every Cloud Run deployment in the project"
if ! gcloud resource-manager org-policies allow \
	run.allowedBinaryAuthorizationPolicies default \
	--project="$project" --quiet; then
	echo >&2
	echo "WARNING: could not set the run.allowedBinaryAuthorizationPolicies organization policy." >&2
	echo "  This needs roles/orgpolicy.policyAdmin, which project ownership alone does not grant." >&2
	echo "  JobTrack releases are still gated: each deployment passes --binary-authorization=default," >&2
	echo "  so the fail-closed policy imported above is enforced against every JobTrack image." >&2
	echo "  What remains open is that another deployment in this project could opt out of Binary" >&2
	echo "  Authorization. Ask an organization policy administrator to run:" >&2
	echo "    gcloud resource-manager org-policies allow \\" >&2
	echo "      run.allowedBinaryAuthorizationPolicies default --project=$project" >&2
	echo >&2
fi

echo "==> Binary Authorization is configured for JobTrack releases"
