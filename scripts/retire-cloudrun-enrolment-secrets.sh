#!/usr/bin/env bash
#
# Disable the three one-time account enrolment credentials after their users have replaced them.
# Disabled versions are recoverable during the retention window and the deployment script will not
# recreate them. This operation must never be used before all three password changes are confirmed.
#
# Usage: ./scripts/retire-cloudrun-enrolment-secrets.sh <gcp-project-id> --confirm-passwords-changed
set -euo pipefail

project="${1:?Usage: $0 <gcp-project-id> --confirm-passwords-changed}"
confirmation="${2:-}"
if [[ $confirmation != --confirm-passwords-changed ]]; then
	echo "ERROR: pass --confirm-passwords-changed only after all three users replaced their enrolment passwords." >&2
	exit 2
fi

export CLOUDSDK_CORE_PROJECT="$project"
enrolment_secrets=(
	jobtrack-account-password-admin
	jobtrack-account-password-user1
	jobtrack-account-password-user2
)

for secret in "${enrolment_secrets[@]}"; do
	if ! gcloud secrets describe "$secret" --project="$project" >/dev/null 2>&1; then
		continue
	fi
	while read -r version; do
		[[ -n $version ]] || continue
		gcloud secrets versions disable "$version" --secret="$secret" \
			--project="$project" --quiet >/dev/null
	done < <(gcloud secrets versions list "$secret" --project="$project" \
		--filter='state=ENABLED' --format='value(name)')
done

echo "==> one-time enrolment secret versions disabled; future deploys will not recreate them"
