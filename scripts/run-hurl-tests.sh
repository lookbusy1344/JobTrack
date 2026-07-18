#!/usr/bin/env bash
# Runs the hurl smoke suite (tests/hurl/*.hurl) against a real, already-running JobTrack.Web host
# -- see docs/operations/hurl-smoke-tests.md for what each suite covers and why this needs a real
# Kestrel process rather than the in-memory WebApplicationFactory the xUnit integration suites use.
#
# Preconditions (README's "Running on a development server" + "Seeding a synthetic end-user
# testing (UAT) scenario"): the database is deployed, bootstrapped, and freshly UAT-seeded, and
# JobTrack.Web is already running at --base-url. This script does not start the host itself, since
# a freshly reseeded database is required every run (the seed and the forced-password-change flow
# this suite drives are both one-time, non-idempotent transitions) -- restarting the host on every
# invocation would hide that precondition rather than enforce it.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

readonly DEFAULT_BASE_URL="http://localhost:5034"
readonly DEFAULT_PROVIDER="sqlite"
readonly DEFAULT_CONNECTION_STRING="Data Source=src/JobTrack.Web/jobtrack-web-dev.db"
readonly DEFAULT_USERNAME="priya.manager"
readonly DEFAULT_PASSWORD="Uat-Seed-Battery-42!"
readonly DEFAULT_NEW_PASSWORD="Hurl-Smoke-Battery-42!"
readonly HURL_TIMEOUT_SECONDS=30
readonly ADMINCLI_TIMEOUT_SECONDS=30

base_url="$DEFAULT_BASE_URL"
provider="$DEFAULT_PROVIDER"
connection_string="$DEFAULT_CONNECTION_STRING"
username="$DEFAULT_USERNAME"
password="$DEFAULT_PASSWORD"
new_password="$DEFAULT_NEW_PASSWORD"

while [[ $# -gt 0 ]]; do
	case "$1" in
		--base-url) base_url="$2"; shift 2 ;;
		--provider) provider="$2"; shift 2 ;;
		--connection-string) connection_string="$2"; shift 2 ;;
		--username) username="$2"; shift 2 ;;
		--password) password="$2"; shift 2 ;;
		--new-password) new_password="$2"; shift 2 ;;
		*) echo "Unknown argument: $1" >&2; exit 1 ;;
	esac
done

if ! curl --silent --fail --output /dev/null "${base_url}/Account/Login"; then
	cat >&2 <<-EOF
		No host reachable at ${base_url}/Account/Login -- this script does not start JobTrack.Web
		itself. Deploy/bootstrap/seed a fresh database, then start the host yourself in another
		terminal (see docs/operations/hurl-smoke-tests.md for the full sequence), e.g.:

		  ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/JobTrack.Web --urls "${base_url}"
	EOF
	exit 1
fi

echo "==> hurl tests/hurl/api-auth-required.hurl"
gtimeout "$HURL_TIMEOUT_SECONDS" hurl --test --variable base_url="$base_url" tests/hurl/api-auth-required.hurl

echo "==> hurl tests/hurl/web-smoke.hurl"
gtimeout "$HURL_TIMEOUT_SECONDS" hurl --test --variable base_url="$base_url" tests/hurl/web-smoke.hurl

echo "==> hurl tests/hurl/web-login-and-csrf.hurl (username=${username})"
gtimeout "$HURL_TIMEOUT_SECONDS" hurl --test \
	--variable base_url="$base_url" \
	--variable username="$username" \
	--variable password="$password" \
	--variable new_password="$new_password" \
	tests/hurl/web-login-and-csrf.hurl

echo "==> JobTrack.AdminCli issue-token --username ${username}"
token_output=$(gtimeout "$ADMINCLI_TIMEOUT_SECONDS" dotnet run --project src/JobTrack.AdminCli -- issue-token \
	--provider "$provider" \
	--connection-string "$connection_string" \
	--username "$username" \
	--label hurl-smoke \
	--lifetime-days 1)
echo "$token_output"
token=$(sed -n "s/^Personal access token for '${username}': //p" <<<"$token_output")
if [[ -z "$token" ]]; then
	echo "Could not extract a token from issue-token's output." >&2
	exit 1
fi

echo "==> hurl tests/hurl/api-bearer-reads.hurl"
gtimeout "$HURL_TIMEOUT_SECONDS" hurl --test --variable base_url="$base_url" --variable token="$token" tests/hurl/api-bearer-reads.hurl

echo "Hurl smoke suite passed."
