#!/usr/bin/env bash
#
# One-time provisioning for the persistent-PostgreSQL deployment: schema, roles, and the three
# accounts. Entrypoint of Dockerfile.postgresql's `provision` target, executed as a Cloud Run job by
# scripts/deploy-cloudrun-postgresql.sh. Full narrative:
# docs/operations/postgresql-cloud-run-deployment.md.
#
# Four steps, each skipped if already done, so the whole script is safe to re-run:
#
#   1. Deploy the schema as the database admin user. JobTrack.Database applies only unapplied
#      schema-version scripts (under its deployment advisory lock), then re-applies the
#      roles-and-grants script and the stored functions -- so this is also how a schema upgrade is
#      applied to an existing database.
#   2. Create the four LOGIN roles, one per connection string Program.cs requires under PostgreSQL
#      (security review remediation §2.6), each a member of exactly one group role.
#   3. Bootstrap the administrator (and, atomically, the permanent root job node). Skipped once
#      initialised_marker holds its row.
#   4. Create the two other employees, as the administrator. Each skipped if the username exists.
#
# NO EXAMPLE JOB NODES ARE IMPORTED. The tree below the root starts empty, deliberately -- there is
# no import-tree step here and the image ships no sample JSON.
#
# Secrets never reach argv. Connection strings are written to umask-077 files and passed as
# --connection-string-file (JobTrack.Database and JobTrack.AdminCli both reject a --connection-string
# containing a password); account passwords are piped to --password-stdin (both reject a plaintext
# --password); role passwords reach psql through the environment via \getenv.

set -euo pipefail
umask 077

readonly SQL_DIR=/app/sql
readonly SCHEMA_ROOT="$SQL_DIR/postgresql/schema-versions"
readonly LOGIN_ROLE_SQL="$SQL_DIR/login-role.sql"
readonly SECRET_DIR=/tmp/jobtrack-provision
readonly DEFAULT_TIME_ZONE=Europe/London
# BootstrapCommand.DefaultHourlyRateAmount, which the administrator gets and which this matches.
readonly DEFAULT_HOURLY_RATE=20

# The four connection strings, and the group role each login role belongs to. Keep in step with the
# table in docs/operations/postgresql-cloud-run-deployment.md §"Credential separation".
readonly DOMAIN_LOGIN_ROLE=jobtrack_domain_login
readonly IDENTITY_LOGIN_ROLE=jobtrack_identity_login
readonly PAT_MANAGEMENT_LOGIN_ROLE=jobtrack_pat_management_login
readonly PAT_AUTHENTICATION_LOGIN_ROLE=jobtrack_pat_authentication_login
# Not one of the four connection strings above -- no running service ever holds this one. It exists
# only so an operator can run emergency-reset.sh (AdminCli reset-password/reset-2fa) against a locked
# or otherwise inaccessible account, via a Cloud Run job execution overriding this image's entrypoint.
readonly EMERGENCY_RESET_LOGIN_ROLE=jobtrack_emergency_reset_login

trap 'rm -rf "$SECRET_DIR"' EXIT

require_env() {
	local name=$1
	if [[ -z ${!name:-} ]]; then
		echo "ERROR: required environment variable $name is not set." >&2
		exit 2
	fi
}

for required in \
	JOBTRACK_DB_HOST JOBTRACK_DB_NAME \
	JOBTRACK_DB_ADMIN_USER JOBTRACK_DB_ADMIN_PASSWORD \
	JOBTRACK_ROLE_PASSWORD_DOMAIN JOBTRACK_ROLE_PASSWORD_IDENTITY \
	JOBTRACK_ROLE_PASSWORD_PAT_MANAGEMENT JOBTRACK_ROLE_PASSWORD_PAT_AUTHENTICATION \
	JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET \
	JOBTRACK_ADMIN_USERNAME JOBTRACK_ADMIN_PASSWORD \
	JOBTRACK_USER1_USERNAME JOBTRACK_USER1_PASSWORD JOBTRACK_USER1_ROLES \
	JOBTRACK_USER2_USERNAME JOBTRACK_USER2_PASSWORD JOBTRACK_USER2_ROLES; do
	require_env "$required"
done

time_zone=${JOBTRACK_TIME_ZONE:-$DEFAULT_TIME_ZONE}
# Passed explicitly even though EmployeeProvisioningDefaults already applies exactly this value: what
# a deployment's accounts are worth per hour should be visible here rather than inherited silently
# from a library constant that could later change.
hourly_rate=${JOBTRACK_DEFAULT_HOURLY_RATE:-$DEFAULT_HOURLY_RATE}

# The standing rotas. The administrator covers a longer day across the whole week; the two other
# accounts keep an ordinary working week.
admin_days=${JOBTRACK_ADMIN_SCHEDULE_DAYS:-Mon,Tue,Wed,Thu,Fri,Sat,Sun}
admin_start=${JOBTRACK_ADMIN_SCHEDULE_START:-08:00}
admin_end=${JOBTRACK_ADMIN_SCHEDULE_END:-20:00}
user_days=${JOBTRACK_USER_SCHEDULE_DAYS:-Mon,Tue,Wed,Thu,Fri}
user_start=${JOBTRACK_USER_SCHEDULE_START:-09:00}
user_end=${JOBTRACK_USER_SCHEDULE_END:-17:00}
admin_display_name=${JOBTRACK_ADMIN_DISPLAY_NAME:-Administrator}
user1_display_name=${JOBTRACK_USER1_DISPLAY_NAME:-$JOBTRACK_USER1_USERNAME}
user2_display_name=${JOBTRACK_USER2_DISPLAY_NAME:-$JOBTRACK_USER2_USERNAME}

# psql reads its connection from the environment, so no password ever appears in a command line.
# JOBTRACK_DB_HOST is the Cloud SQL Unix socket directory (/cloudsql/<project>:<region>:<instance>),
# which libpq and Npgsql both accept as a host, and which PostgreSqlTransportSecurity.Validate exempts
# from the remote-host SSL requirement because the traffic never leaves the instance.
export PGHOST="$JOBTRACK_DB_HOST"
export PGDATABASE="$JOBTRACK_DB_NAME"
export PGUSER="$JOBTRACK_DB_ADMIN_USER"
export PGPASSWORD="$JOBTRACK_DB_ADMIN_PASSWORD"

mkdir -p "$SECRET_DIR"

# Writes one connection string to a private file and echoes its path, for --connection-string-file.
write_connection_file() {
	local role=$1 password=$2
	local path="$SECRET_DIR/$role.conn"
	printf 'Host=%s;Database=%s;Username=%s;Password=%s' \
		"$JOBTRACK_DB_HOST" "$JOBTRACK_DB_NAME" "$role" "$password" >"$path"
	printf '%s' "$path"
}

admin_connection_file=$(write_connection_file "$JOBTRACK_DB_ADMIN_USER" "$JOBTRACK_DB_ADMIN_PASSWORD")
domain_connection_file=$(write_connection_file "$DOMAIN_LOGIN_ROLE" "$JOBTRACK_ROLE_PASSWORD_DOMAIN")

# ---- 1. schema, group roles, grants, stored functions ----------------------
echo "==> deploying schema to $JOBTRACK_DB_NAME"
/app/database/JobTrack.Database deploy \
	--provider postgresql \
	--connection-string-file "$admin_connection_file" \
	--scripts-root "$SCHEMA_ROOT"

# ---- 2. one LOGIN role per connection string -------------------------------
create_login_role() {
	local role=$1 group_role=$2 password=$3
	echo "==> ensuring login role $role (member of $group_role)"
	JOBTRACK_ROLE_NAME="$role" \
		JOBTRACK_GROUP_ROLE="$group_role" \
		JOBTRACK_ROLE_PASSWORD="$password" \
		psql --no-psqlrc --quiet --set=ON_ERROR_STOP=1 --file "$LOGIN_ROLE_SQL"
}

create_login_role "$DOMAIN_LOGIN_ROLE" jobtrack_domain "$JOBTRACK_ROLE_PASSWORD_DOMAIN"
create_login_role "$IDENTITY_LOGIN_ROLE" jobtrack_identity "$JOBTRACK_ROLE_PASSWORD_IDENTITY"
create_login_role "$PAT_MANAGEMENT_LOGIN_ROLE" jobtrack_pat_management "$JOBTRACK_ROLE_PASSWORD_PAT_MANAGEMENT"
create_login_role "$PAT_AUTHENTICATION_LOGIN_ROLE" jobtrack_pat_authentication "$JOBTRACK_ROLE_PASSWORD_PAT_AUTHENTICATION"
create_login_role "$EMERGENCY_RESET_LOGIN_ROLE" jobtrack_emergency_reset "$JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET"

# ---- 3. the administrator, and the permanent root node ---------------------
# bootstrap prompts for display name, time zone, and username on stdin, and reads the password's
# first line ahead of them because --password-stdin is resolved before the command runs -- hence this
# four-line order. AdminCli connects as jobtrack_domain_login: jobtrack_domain is a strict superset of
# what bootstrap and create-employee touch (docs/operations/production-deployment.md).
#
# The ADR 0023 forced password change is deliberately left in place (no --no-force-password-change,
# unlike the SQLite demo image): state persists here, so the generated password is a one-time
# enrolment credential the account holder replaces on first sign-in.
initialised=$(psql --no-psqlrc --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1 \
	--command 'SELECT count(*) FROM initialised_marker')
if [[ $initialised == 0 ]]; then
	echo "==> bootstrapping administrator '$JOBTRACK_ADMIN_USERNAME'"
	printf '%s\n%s\n%s\n%s\n' \
		"$JOBTRACK_ADMIN_PASSWORD" "$admin_display_name" "$time_zone" "$JOBTRACK_ADMIN_USERNAME" |
		/app/admincli/JobTrack.AdminCli bootstrap \
			--provider postgresql \
			--connection-string-file "$domain_connection_file" \
			--password-stdin
else
	echo "==> administrator already bootstrapped; leaving it alone"
fi

# ---- 4. the two other employees --------------------------------------------
create_employee() {
	local username=$1 display_name=$2 roles=$3 password=$4 hourly_rate=$5
	local existing
	# Fed on stdin rather than --command, because psql performs no variable substitution in a
	# --command string -- :'u' would reach the server literally and fail to parse. \getenv plus the
	# :'u' quoted-literal form keeps a configured username from breaking out of the string.
	# ASP.NET Core Identity's normalizer upper-cases invariantly.
	existing=$(JOBTRACK_LOOKUP_USERNAME="$username" psql --no-psqlrc --quiet --tuples-only --no-align \
		--set=ON_ERROR_STOP=1 <<-'SQL'
			\getenv u JOBTRACK_LOOKUP_USERNAME
			SELECT count(*) FROM identity_user WHERE normalized_user_name = upper(:'u');
		SQL
	)
	if [[ $existing != 0 ]]; then
		echo "==> employee '$username' already exists; leaving it alone"
		return 0
	fi

	echo "==> creating employee '$username' ($roles)"
	printf '%s\n' "$password" |
		/app/admincli/JobTrack.AdminCli create-employee \
			--provider postgresql \
			--connection-string-file "$domain_connection_file" \
			--actor "$JOBTRACK_ADMIN_USERNAME" \
			--username "$username" \
			--password-stdin \
			--display-name "$display_name" \
			--roles "$roles" \
			--iana-time-zone "$time_zone" \
			--default-hourly-rate "$hourly_rate"
}

create_employee "$JOBTRACK_USER1_USERNAME" "$user1_display_name" "$JOBTRACK_USER1_ROLES" \
	"$JOBTRACK_USER1_PASSWORD" "$hourly_rate"
create_employee "$JOBTRACK_USER2_USERNAME" "$user2_display_name" "$JOBTRACK_USER2_ROLES" \
	"$JOBTRACK_USER2_PASSWORD" "$hourly_rate"

# ---- 5. the standing rotas --------------------------------------------------
# Every account already carries EmployeeProvisioningDefaults' Mon-Fri 09:00-17:00 from the moment it
# is created, so this replaces that placeholder with the pattern this deployment actually wants --
# set-schedule corrects the provisioned version in place rather than adding a second one beside it.
#
# Re-running is safe and idempotent in effect: correcting a version to the values it already holds
# changes nothing an operator would notice. It refuses outright once an account has more than one
# version or any exception, so a rota someone has since edited through the Rota pages is never
# silently overwritten by a redeploy.
set_schedule() {
	local username=$1 days=$2 start=$3 end=$4
	echo "==> setting the rota for '$username' ($days $start-$end)"
	/app/admincli/JobTrack.AdminCli set-schedule \
		--provider postgresql \
		--connection-string-file "$domain_connection_file" \
		--actor "$JOBTRACK_ADMIN_USERNAME" \
		--username "$username" \
		--days "$days" \
		--start "$start" \
		--end "$end" \
		--iana-time-zone "$time_zone"
}

set_schedule "$JOBTRACK_ADMIN_USERNAME" "$admin_days" "$admin_start" "$admin_end"
set_schedule "$JOBTRACK_USER1_USERNAME" "$user_days" "$user_start" "$user_end"
set_schedule "$JOBTRACK_USER2_USERNAME" "$user_days" "$user_start" "$user_end"

echo "==> provisioning complete (no example job nodes were imported)"
