#!/usr/bin/env bash
#
# Ad hoc emergency account recovery: builds the jobtrack_emergency_reset_login connection string and
# runs AdminCli reset-password or reset-2fa against it. Not part of the normal provisioning sequence
# (provision.sh already creates the login role every run) -- this is the command an operator overrides
# the jobtrack-provision job's entrypoint with when an account is genuinely locked out or has lost its
# authenticator device. See docs/operations/postgresql-cloud-run-deployment.md §"Recovering a locked
# or inaccessible account" for the exact `gcloud run jobs execute --command --args` invocation.
#
# Usage: emergency-reset.sh password <username>
#        emergency-reset.sh two-factor <username>

set -euo pipefail
umask 077

readonly SECRET_DIR=/tmp/jobtrack-emergency-reset
trap 'rm -rf "$SECRET_DIR"' EXIT

mode="${1:?Usage: $0 {password|two-factor} <username>}"
username="${2:?Usage: $0 {password|two-factor} <username>}"

require_env() {
	local name=$1
	if [[ -z ${!name:-} ]]; then
		echo "ERROR: required environment variable $name is not set." >&2
		exit 2
	fi
}

for required in JOBTRACK_DB_HOST JOBTRACK_DB_NAME JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET; do
	require_env "$required"
done

mkdir -p "$SECRET_DIR"
connection_file="$SECRET_DIR/emergency-reset.conn"
printf 'Host=%s;Database=%s;Username=jobtrack_emergency_reset_login;Password=%s' \
	"$JOBTRACK_DB_HOST" "$JOBTRACK_DB_NAME" "$JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET" >"$connection_file"

case "$mode" in
password)
	exec /app/admincli/JobTrack.AdminCli reset-password \
		--provider postgresql \
		--connection-string-file "$connection_file" \
		--username "$username"
	;;
two-factor)
	exec /app/admincli/JobTrack.AdminCli reset-2fa \
		--provider postgresql \
		--connection-string-file "$connection_file" \
		--username "$username"
	;;
*)
	echo "ERROR: unknown mode '$mode' -- expected 'password' or 'two-factor'." >&2
	exit 2
	;;
esac
