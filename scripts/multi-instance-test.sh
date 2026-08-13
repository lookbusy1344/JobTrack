#!/usr/bin/env bash
#
# ADR 0066 Stage 7: opt-in local proof that the MultiInstance topology actually works on a real
# container runtime (OrbStack or Docker Engine) -- docker/compose.multi-instance.yml brings up one
# disposable PostgreSQL container, two independent JobTrack.Web instances (web-a, web-b) built from
# the same immutable image, and a round-robin HTTPS proxy with no session affinity. This script
# builds the images, provisions the database, waits for both hosts to report ready, then drives the
# full black-box scenario from plan §5's evidence matrix -- cross-host auth, antiforgery, filter
# recall, PAT delivery, both rate limiters, and a concurrent domain-write race -- while alternating
# which host receives each request.
#
# Opt-in only: never added to fast-test.sh (image builds and a PostgreSQL container exceed that
# lane's contract, CLAUDE.md commit gate). Idempotent per run -- every credential is generated fresh
# into a mode-700 temporary directory and discarded on exit; nothing here is committed or printed.
#
# Usage: ./scripts/multi-instance-test.sh
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
cd "$repo"

compose_file="docker/compose.multi-instance.yml"
project_name="jobtrack-multi-instance"
host_a_port=8081
host_b_port=8082
proxy_port=8443
readiness_timeout_seconds=180

for required_command in docker jq openssl curl hurl; do
	if ! command -v "$required_command" >/dev/null 2>&1; then
		echo "ERROR: required command '$required_command' is not on PATH." >&2
		exit 1
	fi
done

compose() {
	docker compose -f "$compose_file" "$@"
}

secret_dir="$(mktemp -d "${TMPDIR:-/tmp}/jobtrack-multi-instance-test.XXXXXX")"
chmod 700 "$secret_dir"
export JOBTRACK_MULTI_INSTANCE_SECRET_DIR="$secret_dir"

# Populated once real values exist, so the failure-log collector below can redact them even if it
# runs before every secret is generated (an early failure during image build, say).
declare -a redact_values=()

cleanup() {
	local exit_code=$?
	if [[ $exit_code != 0 ]]; then
		echo "==> FAILED -- collecting container logs (secrets redacted)" >&2
		local redacted_log="$secret_dir/failure-logs.txt"
		compose logs --no-color >"$redacted_log" 2>&1 || true
		local value
		for value in "${redact_values[@]}"; do
			[[ -n $value ]] || continue
			# BSD sed (macOS) and GNU sed both accept -i with an explicit (possibly empty) suffix
			# argument in this form; '#' avoids clashing with '/' in connection strings/paths.
			sed -i.bak "s#$(printf '%s' "$value" | sed 's/[.[\*^$/]/\\&/g')#[REDACTED]#g" "$redacted_log" 2>/dev/null || true
			rm -f "$redacted_log.bak"
		done
		echo "==> redacted container logs: $redacted_log" >&2
		cat "$redacted_log" >&2
	fi

	echo "==> removing only this topology's own containers, network, and volumes"
	compose down --volumes --remove-orphans --timeout 5 >/dev/null 2>&1 || true
	rm -rf "$secret_dir"
	exit "$exit_code"
}
trap cleanup EXIT

generate_password() {
	openssl rand -base64 48 | tr -d '/+=\n' | cut -c1-24
}

# ---- synthetic credentials, certificates, and the two env-files docker/provision.sh and
# Dockerfile.postgresql's serve image expect -------------------------------------------------------

db_admin_password="$(generate_password)"
role_password_domain="$(generate_password)"
role_password_history_deletion="$(generate_password)"
role_password_credential_administration="$(generate_password)"
role_password_identity="$(generate_password)"
role_password_pat_management="$(generate_password)"
role_password_pat_authentication="$(generate_password)"
role_password_emergency_reset="$(generate_password)"
admin_username="compose.admin"
admin_password="$(generate_password)"
admin_new_password="$(generate_password)"
user1_username="compose.worker1"
user1_password="$(generate_password)"
user1_new_password="$(generate_password)"
user2_username="compose.worker2"
user2_password="$(generate_password)"
user2_new_password="$(generate_password)"
data_protection_certificate_password="$(generate_password)"

redact_values=(
	"$db_admin_password" "$role_password_domain" "$role_password_history_deletion" "$role_password_credential_administration" "$role_password_identity"
	"$role_password_pat_management" "$role_password_pat_authentication" "$role_password_emergency_reset"
	"$admin_password" "$admin_new_password" "$user1_password" "$user1_new_password" "$user2_password" "$user2_new_password"
	"$data_protection_certificate_password"
)

printf '%s' "$db_admin_password" >"$secret_dir/db-admin-password"

# Unix-domain socket, not TCP+TLS -- see compose.multi-instance.yml's own comment on why.
db_host="/var/run/postgresql"
db_name="jobtrack"
connection_string() {
	local role=$1 password=$2
	printf 'Host=%s;Database=%s;Username=%s;Password=%s' "$db_host" "$db_name" "$role" "$password"
}

cat >"$secret_dir/provision.env" <<EOF
JOBTRACK_DB_HOST=$db_host
JOBTRACK_DB_NAME=$db_name
JOBTRACK_DB_ADMIN_USER=postgres
JOBTRACK_DB_ADMIN_PASSWORD=$db_admin_password
JOBTRACK_ROLE_PASSWORD_DOMAIN=$role_password_domain
JOBTRACK_ROLE_PASSWORD_HISTORY_DELETION=$role_password_history_deletion
JOBTRACK_ROLE_PASSWORD_CREDENTIAL_ADMINISTRATION=$role_password_credential_administration
JOBTRACK_ROLE_PASSWORD_IDENTITY=$role_password_identity
JOBTRACK_ROLE_PASSWORD_PAT_MANAGEMENT=$role_password_pat_management
JOBTRACK_ROLE_PASSWORD_PAT_AUTHENTICATION=$role_password_pat_authentication
JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET=$role_password_emergency_reset
JOBTRACK_ADMIN_USERNAME=$admin_username
JOBTRACK_ADMIN_PASSWORD=$admin_password
JOBTRACK_USER1_USERNAME=$user1_username
JOBTRACK_USER1_PASSWORD=$user1_password
JOBTRACK_USER1_ROLES=Worker
JOBTRACK_USER2_USERNAME=$user2_username
JOBTRACK_USER2_PASSWORD=$user2_password
JOBTRACK_USER2_ROLES=Worker
EOF

# RateLimiting__*PermitLimit deliberately small and the windows deliberately long: the black-box
# scenario below must exhaust each limiter in a handful of requests well inside one fixed window, the
# same reasoning as TwoHostPostgreSqlAcceptanceTests' own PermitLimit=2. AllowedHosts covers every
# access path this script uses -- "localhost" for the direct host-A/B ports and the proxy (ASP.NET
# Core's host filter compares only the host portion of the Host header, ignoring port), plus the
# compose service names themselves for wait-for-web's internal /health/ready probe, which addresses
# each host by its Docker DNS name rather than through the proxy.
cat >"$secret_dir/web.env" <<EOF
Deployment__Topology=MultiInstance
DataProtection__Store=PostgreSql
RateLimiting__Store=PostgreSql
RateLimiting__LoginPermitLimit=3
RateLimiting__LoginWindowSeconds=120
RateLimiting__ApiPermitLimit=5
RateLimiting__ApiWindowSeconds=120
AllowedHosts=localhost;web-a;web-b
ForwardedHeaders__KnownNetworks__0=0.0.0.0/0
ConnectionStrings__JobTrackDomain=$(connection_string jobtrack_domain_login "$role_password_domain")
ConnectionStrings__JobTrackHistoryDeletion=$(connection_string jobtrack_history_deletion_login "$role_password_history_deletion")
ConnectionStrings__JobTrackCredentialAdministration=$(connection_string jobtrack_credential_administration_login "$role_password_credential_administration")
ConnectionStrings__JobTrackIdentity=$(connection_string jobtrack_identity_login "$role_password_identity")
ConnectionStrings__JobTrackPatManagement=$(connection_string jobtrack_pat_management_login "$role_password_pat_management")
ConnectionStrings__JobTrackPatAuthentication=$(connection_string jobtrack_pat_authentication_login "$role_password_pat_authentication")
EOF

echo "==> generating the data-protection certificate (shared read-only between web-a and web-b)"
printf '%s' "$data_protection_certificate_password" >"$secret_dir/data-protection-password"
openssl req -x509 -newkey rsa:3072 -sha256 -days 1 -nodes \
	-subj '/CN=JobTrack multi-instance compose data-protection key encryptor' \
	-keyout "$secret_dir/data-protection.key" -out "$secret_dir/data-protection.crt" >/dev/null 2>&1
openssl pkcs12 -export -name JobTrackDataProtection \
	-inkey "$secret_dir/data-protection.key" -in "$secret_dir/data-protection.crt" \
	-out "$secret_dir/data-protection.pfx" -passout "file:$secret_dir/data-protection-password"

echo "==> generating the proxy's ephemeral TLS certificate"
openssl req -x509 -newkey rsa:2048 -sha256 -days 1 -nodes \
	-subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost' \
	-keyout "$secret_dir/proxy-tls.key" -out "$secret_dir/proxy-tls.crt" >/dev/null 2>&1

# ---- build, provision, start ------------------------------------------------------------------

echo "==> building images"
compose build

echo "==> starting postgres, provisioning, then web-a/web-b/proxy (each gated by the previous step)"
compose up -d --wait --wait-timeout "$readiness_timeout_seconds"

echo "==> waiting for both direct host ports and the proxy to answer /health/ready"
wait_ready() {
	local url=$1 deadline
	deadline=$((SECONDS + readiness_timeout_seconds))
	until curl --silent --fail --max-time 2 --insecure --header 'X-Forwarded-Proto: https' "$url" >/dev/null; do
		if ((SECONDS > deadline)); then
			echo "ERROR: $url never became ready within ${readiness_timeout_seconds}s." >&2
			return 1
		fi
		sleep 1
	done
}
wait_ready "http://localhost:$host_a_port/health/ready"
wait_ready "http://localhost:$host_b_port/health/ready"
wait_ready "https://localhost:$proxy_port/health/ready"

host_a="http://localhost:$host_a_port"
host_b="http://localhost:$host_b_port"

# ---- cookie/session-shaped scenarios (tests/hurl/multi-instance-scenario.hurl) -----------------

echo "==> running the cross-host cookie scenario (auth, antiforgery, filter recall, PAT delivery)"
scenario_result="$(hurl --test --json --header "X-Forwarded-Proto: https" \
	--variable host_a="$host_a" --variable host_b="$host_b" \
	--variable admin_username="$admin_username" --variable admin_password="$admin_password" \
	--variable admin_new_password="$admin_new_password" \
	tests/hurl/multi-instance-scenario.hurl)"
race_leaf_id="$(jq -r '.entries[] | .captures[]? | select(.name == "race_leaf_id") | .value' <<<"$scenario_result")"
if [[ -z $race_leaf_id ]]; then
	echo "ERROR: could not capture race_leaf_id from the cross-host scenario." >&2
	exit 1
fi
# Minted by the same hurl run above, admin's already-signed-in session -- avoids a second login,
# which would no longer redirect through ChangePassword once the first login already cleared ADR
# 0023's forced-change flag.
api_token="$(jq -r '.entries[] | .captures[]? | select(.name == "api_rate_limit_token") | .value' <<<"$scenario_result")"
if [[ -z $api_token ]]; then
	echo "ERROR: could not capture api_rate_limit_token from the cross-host scenario." >&2
	exit 1
fi

# ---- rate limiting (loop-until-429 -- hurl cannot express this, so plain curl reusing hurl's own
# cookie-jar file format) -------------------------------------------------------------------------

echo "==> login rate limiter: alternating hosts past RateLimiting:LoginPermitLimit=3"
login_jar="$secret_dir/login-cookies.txt"
login_token_result="$(hurl --json --header "X-Forwarded-Proto: https" --cookie-jar "$login_jar" \
	--variable host="$host_a" tests/hurl/multi-instance-antiforgery-token.hurl)"
login_token="$(jq -r '.entries[0].captures[] | select(.name == "login_token") | .value' <<<"$login_token_result")"

login_attempt() {
	local host=$1
	curl --silent --output /dev/null --write-out '%{http_code}' \
		--header 'X-Forwarded-Proto: https' --cookie "$login_jar" --cookie-jar "$login_jar" \
		--data-urlencode "__RequestVerificationToken=$login_token" \
		--data-urlencode "Input.UserName=ratelimit.probe" \
		--data-urlencode "Input.Password=Wrong-Password-1!" \
		"$host/Account/Login"
}
expected_login_codes=(200 200 200 429)
for index in "${!expected_login_codes[@]}"; do
	host=$host_a
	if ((index % 2 == 1)); then host=$host_b; fi
	actual="$(login_attempt "$host")"
	expected="${expected_login_codes[$index]}"
	if [[ $actual != "$expected" ]]; then
		echo "ERROR: login attempt $((index + 1)) against $host returned $actual, expected $expected." >&2
		exit 1
	fi
done
echo "    global login limit is exact across hosts (3 admitted, the 4th denied)"

echo "==> API rate limiter: alternating hosts past RateLimiting:ApiPermitLimit=5"
# Use a fixed synthetic client address with no credentials so this probe owns a partition untouched
# by the preceding scenario. Reusing admin's authenticated partition made the expected count depend
# on whether that scenario's earlier API request landed before or after a fixed-window boundary.
# The focused two-host integration test separately proves authenticated requests partition by user.
api_attempt() {
	local host=$1
	curl --silent --output /dev/null --write-out '%{http_code}' \
		--header 'X-Forwarded-Proto: https' --header 'X-Forwarded-For: 192.0.2.1' \
		"$host/api/jobs/root"
}
expected_api_codes=(401 401 401 401 401 429)
for index in "${!expected_api_codes[@]}"; do
	host=$host_a
	if ((index % 2 == 1)); then host=$host_b; fi
	actual="$(api_attempt "$host")"
	expected="${expected_api_codes[$index]}"
	if [[ $actual != "$expected" ]]; then
		echo "ERROR: API attempt $((index + 1)) against $host returned $actual, expected $expected." >&2
		exit 1
	fi
done
echo "    global API limit is exact across hosts (5 unauthorized requests admitted to authorization, the next denied)"

# ---- concurrent domain write race (two distinct actors, two hosts, one leaf) -------------------

echo "==> concurrent pickup race: worker1 on host A vs worker2 on host B, same leaf"
worker1_pat_result="$(hurl --test --json --header "X-Forwarded-Proto: https" \
	--variable host="$host_a" --variable username="$user1_username" --variable password="$user1_password" \
	--variable new_password="$user1_new_password" --variable label="race-worker1" \
	tests/hurl/multi-instance-mint-pat.hurl)"
worker1_pat="$(jq -r '.entries[] | .captures[]? | select(.name == "pat_token") | .value' <<<"$worker1_pat_result")"

worker2_pat_result="$(hurl --test --json --header "X-Forwarded-Proto: https" \
	--variable host="$host_b" --variable username="$user2_username" --variable password="$user2_password" \
	--variable new_password="$user2_new_password" --variable label="race-worker2" \
	tests/hurl/multi-instance-mint-pat.hurl)"
worker2_pat="$(jq -r '.entries[] | .captures[]? | select(.name == "pat_token") | .value' <<<"$worker2_pat_result")"

race_code_a_file="$secret_dir/race-a-code"
race_code_b_file="$secret_dir/race-b-code"
curl --silent --output /dev/null --write-out '%{http_code}' \
	--header 'X-Forwarded-Proto: https' --header "Authorization: Bearer $worker1_pat" \
	--request POST "$host_a/api/jobs/$race_leaf_id/pickup" >"$race_code_a_file" &
curl --silent --output /dev/null --write-out '%{http_code}' \
	--header 'X-Forwarded-Proto: https' --header "Authorization: Bearer $worker2_pat" \
	--request POST "$host_b/api/jobs/$race_leaf_id/pickup" >"$race_code_b_file" &
wait

race_code_a="$(cat "$race_code_a_file")"
race_code_b="$(cat "$race_code_b_file")"
race_codes_sorted="$(printf '%s\n%s\n' "$race_code_a" "$race_code_b" | sort | tr '\n' ' ')"
if [[ $race_codes_sorted != "200 409 " ]]; then
	echo "ERROR: expected exactly one 200 and one 409 from the concurrent pickup race, got: $race_codes_sorted" >&2
	exit 1
fi
echo "    exactly one winner (200) and one conflict (409) across host A and host B"

echo ""
echo "Multi-instance scenario passed: cross-host auth, antiforgery, filter recall, PAT delivery,"
echo "both rate limiters, and the concurrent domain-write race all held across host A, host B, and"
echo "the round-robin proxy."
