#!/usr/bin/env bash
#
# Deploy JobTrack to Cloud Run against a persistent Cloud SQL PostgreSQL instance.
#
# This is the second deployment path, alongside -- not replacing -- ./deploy-cloudrun.sh, which
# deploys the SQLite demo image and is unchanged. The difference that matters: nothing here lives in
# the container, so a recycle, redeploy, or scale-to-zero loses nothing. See
# ../docs/operations/postgresql-cloud-run-deployment.md for the full narrative, including why the
# database is a managed instance rather than a process inside the image.
#
# It installs NO example job nodes and creates exactly THREE accounts -- an administrator and two
# others -- each with a randomly generated password stored in Secret Manager. All three force a
# password change on first sign-in (the ADR 0023 default), so those values are one-time enrolment
# credentials, not standing passwords.
#
# Idempotent and re-runnable. Every resource is created only if absent, and every secret keeps its
# existing value rather than being regenerated, so a second run applies any new schema versions and
# redeploys the current build without locking you out.
#
# Usage: ./scripts/deploy-cloudrun-postgresql.sh <gcp-project-id> [region]
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
monorepo_root="$(cd "$repo/.." && pwd)"

project="${1:?Usage: $0 <gcp-project-id> [region]}"
# europe-west1 (Belgium) is a Tier 1 GCP pricing region; europe-west2 (London) is Tier 2, so the
# Always Free allowance and per-unit cost are both worse there for no functional gain.
region="${2:-europe-west1}"

service="jobtrack-web-pg"
provision_job="jobtrack-provision"
sql_instance="jobtrack-pg"
sql_database="jobtrack"
repository="cloud-run-source-deploy"
source_revision="$(git -C "$repo" rev-parse --verify HEAD)"
# The build stage's own `git describe` fallback always fails inside the container (no .git in the
# build context), so the login page's build-revision chip needs the real value passed in from here.
source_revision_id="$(git -C "$repo" describe --tags --always --dirty --abbrev=12)"
build_nonce="$(openssl rand -hex 4)"
build_id="${source_revision:0:12}-$(date -u +%Y%m%d%H%M%S)-$build_nonce"
serve_image="$region-docker.pkg.dev/$project/$repository/$service:$build_id"
provision_image="$region-docker.pkg.dev/$project/$repository/$provision_job:$build_id"
release_attestor="jobtrack-release"
release_keyring="jobtrack-release"
release_key="image-attestation"
release_key_version="1"
key_bucket="$project-jobtrack-dpkeys"
key_volume="dpkeys"
key_mount_path="/var/lib/jobtrack/keys"
# Separate directories, not two files in one: Cloud Run backs each secret file mount with its own
# directory volume and refuses a second, different secret mounted into a directory already in use.
certificate_mount_path="/var/run/secrets/jobtrack/certificate/data-protection.pfx"
certificate_password_mount_path="/var/run/secrets/jobtrack/certificate-password/data-protection-password"
orbstack_socket="${HOME}/.orbstack/run/docker.sock"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/jobtrack-cloudrun-deploy.XXXXXX")"
database_password_flags="$temporary_directory/database-password-flags.yaml"
provision_job_may_exist=false
provision_access_may_exist=false
provision_access_condition=
# Every temporary provisioning grant is titled with this prefix plus the run's own nonce, so a grant
# can be recognised as JobTrack's -- and as belonging to some *other* run -- from the policy alone.
provision_access_title_prefix="jobtrack-provision-"

# The secrets the provisioning job reads. One list, used by both the grant and the two revoke paths,
# so they cannot drift apart and strand a grant nothing removes.
provision_secrets=(
	jobtrack-db-admin-password
	jobtrack-role-password-domain
	jobtrack-role-password-identity
	jobtrack-role-password-pat-management
	jobtrack-role-password-pat-authentication
	jobtrack-role-password-emergency-reset
	jobtrack-account-password-admin
	jobtrack-account-password-user1
	jobtrack-account-password-user2
)

delete_provision_job() {
	if [[ $provision_job_may_exist == true ]]; then
		gcloud run jobs delete "$provision_job" \
			--project="$project" --region="$region" --quiet >/dev/null 2>&1 || true
		provision_job_may_exist=false
	fi
}

revoke_provision_access() {
	if [[ $provision_access_may_exist == true ]]; then
		local condition=None
		if [[ -n $provision_access_condition ]]; then
			condition="$provision_access_condition"
		fi

		for secret in "${provision_secrets[@]}"; do
			gcloud secrets remove-iam-policy-binding "$secret" \
				--project="$project" \
				--member="serviceAccount:$provision_service_account" \
				--role=roles/secretmanager.secretAccessor --condition="$condition" --quiet >/dev/null 2>&1 || true
		done
		gcloud projects remove-iam-policy-binding "$project" \
			--member="serviceAccount:$provision_service_account" \
			--role=roles/cloudsql.client --condition="$condition" --quiet >/dev/null 2>&1 || true
		provision_access_may_exist=false
	fi
}

# revoke_provision_access can only remove the condition *this* run constructed, because
# remove-iam-policy-binding matches a conditional binding exactly and each run's title carries its own
# nonce. A run killed between granting and revoking -- SIGKILL, a lost workstation, anything the EXIT
# trap cannot survive -- therefore leaves a binding no later run will ever match, and they accumulate
# as dead policy entries. (Their expiry still bounds the privilege, which was the point of the
# condition; this is about the policy not filling with corpses.) So reconcile by reading the policy
# back and removing whatever provisioning conditions are actually there.
stale_provision_conditions() {
	jq -c \
		--arg member "serviceAccount:$provision_service_account" \
		--arg role "$1" \
		--arg prefix "$provision_access_title_prefix" '
		(.bindings // [])[]
		| select(.role == $role and (((.members // []) | index($member)) != null))
		| .condition // empty
		| select((.title // "") | startswith($prefix))'
}

# Usage: sweep_stale_provision_bindings <gcloud-group> <resource> <role> [extra gcloud flags...]
sweep_stale_provision_bindings() {
	local group=$1 resource=$2 role=$3
	shift 3
	local condition_file="$temporary_directory/stale-provision-condition.json" condition

	while read -r condition; do
		[[ -n $condition ]] || continue
		printf '%s\n' "$condition" >"$condition_file"
		echo "    removing a $role binding leaked by an interrupted earlier run"
		# --condition-from-file rather than the inline --condition: the inline form is comma-separated,
		# so it cannot express a title, description, or expression that itself contains a comma.
		gcloud "$group" remove-iam-policy-binding "$resource" "$@" \
			--member="serviceAccount:$provision_service_account" \
			--role="$role" --condition-from-file="$condition_file" --quiet >/dev/null 2>&1 || true
	done < <(gcloud "$group" get-iam-policy "$resource" "$@" --format=json 2>/dev/null |
		stale_provision_conditions "$role")

	rm -f "$condition_file"
}

cleanup() {
	delete_provision_job
	revoke_provision_access
	rm -rf "$temporary_directory"
}

trap cleanup EXIT

# The three accounts. Roles are EmployeeRole names; the first is the account's initial role and any
# remainder are granted afterwards (ADR 0023).
#
# admin_username is deliberately NOT "admin", unlike deploy-cloudrun.sh's SQLite demo default: the two
# images are separate databases with no technical conflict, but sharing a username across both makes it
# easy to confuse or overwrite one deployment's admin credential with the other's when juggling both.
admin_username="adminpg"
admin_display_name="Administrator"
user1_username="manager"
user1_display_name="Job Manager"
user1_roles="JobManager,Worker"
user2_username="worker"
user2_display_name="Worker"
user2_roles="Worker"
time_zone="Europe/London"

# db-f1-micro is the cheapest shared-core Enterprise tier. Unlike the Cloud Run service, a Cloud SQL
# instance does not scale to zero -- it bills continuously from creation until deletion.
sql_tier="db-f1-micro"
sql_version="POSTGRES_18"
sql_backup_start_time="03:00"

# ---- preflight --------------------------------------------------------------

for required_command in curl docker gcloud git jq openssl; do
	if ! command -v "$required_command" >/dev/null 2>&1; then
		echo "ERROR: required command '$required_command' is not installed." >&2
		exit 1
	fi
done

if [[ -n $(git -C "$repo" status --porcelain -- .) ]]; then
	echo "ERROR: the JobTrack source tree is dirty; commit the exact release source before deploying." >&2
	exit 1
fi

if ! docker info >/dev/null 2>&1; then
	if [[ -S "$orbstack_socket" ]]; then
		echo "ERROR: Docker is configured but not responding via the OrbStack socket at $orbstack_socket." >&2
		echo "Start OrbStack, then rerun this script." >&2
	else
		echo "ERROR: Docker is not available, and the expected OrbStack socket was not found at $orbstack_socket." >&2
		echo "Start OrbStack or another local Docker daemon, then rerun this script." >&2
	fi

	exit 1
fi

echo "==> enabling required APIs"
gcloud services enable \
	run.googleapis.com \
	sqladmin.googleapis.com \
	secretmanager.googleapis.com \
	artifactregistry.googleapis.com \
	containerscanning.googleapis.com \
	ondemandscanning.googleapis.com \
	binaryauthorization.googleapis.com \
	containeranalysis.googleapis.com \
	cloudkms.googleapis.com \
	--project="$project" --quiet

# Three dedicated service accounts, NOT the default compute service account. That default is created
# with the project Editor role in most projects, so running a public web app as it would mean an
# application compromise carries write access to every resource in the project. These start with no
# roles at all and are granted exactly what each needs, nothing more.
#
# They are separated by purpose. Runtime access is durable; provisioning and emergency access are
# granted only around their transient job execution and revoked by an EXIT trap.
run_service_account="jobtrack-run@$project.iam.gserviceaccount.com"
provision_service_account="jobtrack-provision-sa@$project.iam.gserviceaccount.com"
emergency_service_account="jobtrack-emergency-reset@$project.iam.gserviceaccount.com"

ensure_service_account() {
	local account=$1 display=$2
	if gcloud iam service-accounts describe "$account" --project="$project" >/dev/null 2>&1; then
		return 0
	fi

	gcloud iam service-accounts create "${account%%@*}" \
		--project="$project" --display-name="$display" --quiet >/dev/null
}

echo "==> ensuring dedicated service accounts exist"
ensure_service_account "$run_service_account" "JobTrack Cloud Run service"
ensure_service_account "$provision_service_account" "JobTrack provisioning job"
ensure_service_account "$emergency_service_account" "JobTrack emergency account recovery"

# Reconcile residue from an interrupted or older deployment before touching credentials. The EXIT
# trap cannot run after SIGKILL or a workstation loss, so the next invocation must not assume it did.
provision_job_may_exist=true
provision_access_may_exist=true
delete_provision_job
revoke_provision_access

# revoke_provision_access has just removed any *unconditional* residue (provision_access_condition is
# still empty here). Conditional bindings left by an interrupted run carry another run's nonce and can
# only be found by reading the policy back.
echo "==> reconciling provisioning grants left by any interrupted earlier run"
sweep_stale_provision_bindings "projects" "$project" roles/cloudsql.client
for secret in "${provision_secrets[@]}"; do
	sweep_stale_provision_bindings \
		"secrets" "$secret" roles/secretmanager.secretAccessor --project="$project"
done

# ---- secrets ----------------------------------------------------------------
# Generated from a strictly alphanumeric alphabet: no ';' or '=' to break a connection string, and 24
# characters is well past PasswordPolicy.MinimumLength (15).
generate_password() {
	openssl rand -base64 48 | tr -d '/+=\n' | cut -c1-24
}

secret_exists() {
	gcloud secrets describe "$1" --project="$project" >/dev/null 2>&1
}

read_secret() {
	gcloud secrets versions access latest --secret="$1" --project="$project"
}

# Echoes the secret's value, generating and storing one on first run only -- so re-running this
# script never rotates a credential out from under the running deployment.
ensure_generated_secret() {
	local name=$1
	if secret_exists "$name"; then
		read_secret "$name"
		return 0
	fi

	local value
	value="$(generate_password)"
	printf '%s' "$value" |
		gcloud secrets create "$name" --project="$project" --replication-policy=automatic --data-file=- >/dev/null
	printf '%s' "$value"
}

# Stores a derived value (a connection string), adding a version only when it actually changed.
put_secret() {
	local name=$1 value=$2
	if secret_exists "$name"; then
		if [[ "$(read_secret "$name")" == "$value" ]]; then
			return 0
		fi

		printf '%s' "$value" | gcloud secrets versions add "$name" --project="$project" --data-file=- >/dev/null
		return 0
	fi

	printf '%s' "$value" |
		gcloud secrets create "$name" --project="$project" --replication-policy=automatic --data-file=- >/dev/null
}

secret_version() {
	gcloud secrets versions list "$1" \
		--project="$project" --filter='state=ENABLED' --sort-by='~createTime' --limit=1 --format='value(name)'
}

create_data_protection_certificate() {
	local private_key="$temporary_directory/data-protection.key"
	local certificate="$temporary_directory/data-protection.crt"
	local archive="$temporary_directory/data-protection.pfx"
	local password_file="$temporary_directory/data-protection-password"
	printf '%s' "$data_protection_certificate_password" >"$password_file"
	openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes \
		-subj '/CN=JobTrack data-protection key encryptor' \
		-keyout "$private_key" -out "$certificate" >/dev/null 2>&1
	openssl pkcs12 -export -name JobTrackDataProtection \
		-inkey "$private_key" -in "$certificate" -out "$archive" -passout "file:$password_file"
	gcloud secrets create jobtrack-data-protection-certificate \
		--project="$project" --replication-policy=automatic --data-file="$archive" >/dev/null
}

ensure_data_protection_material() {
	local certificate_exists=false password_exists=false archive password_file
	if secret_exists jobtrack-data-protection-certificate; then
		certificate_exists=true
	fi
	if secret_exists jobtrack-data-protection-certificate-password; then
		password_exists=true
	fi

	if [[ $certificate_exists != "$password_exists" ]]; then
		echo "ERROR: the data-protection certificate and its password secret must either both exist or both be absent." >&2
		echo "Restore the missing member from backup; generating a replacement could make the persisted key ring unreadable." >&2
		return 1
	fi

	if [[ $certificate_exists == false ]]; then
		data_protection_certificate_password="$(generate_password)"
		printf '%s' "$data_protection_certificate_password" |
			gcloud secrets create jobtrack-data-protection-certificate-password \
				--project="$project" --replication-policy=automatic --data-file=- >/dev/null
		create_data_protection_certificate
		return 0
	fi

	data_protection_certificate_password="$(read_secret jobtrack-data-protection-certificate-password)"
	archive="$temporary_directory/data-protection-existing.pfx"
	password_file="$temporary_directory/data-protection-existing-password"
	printf '%s' "$data_protection_certificate_password" >"$password_file"
	gcloud secrets versions access latest --secret=jobtrack-data-protection-certificate \
		--project="$project" --out-file="$archive" >/dev/null
	if ! openssl pkcs12 -in "$archive" -passin "file:$password_file" -noout >/dev/null 2>&1; then
		echo "ERROR: the stored data-protection certificate cannot be opened with its stored password." >&2
		return 1
	fi
}

echo "==> ensuring secrets exist (existing values are preserved, never regenerated)"
db_admin_password="$(ensure_generated_secret jobtrack-db-admin-password)"
role_password_domain="$(ensure_generated_secret jobtrack-role-password-domain)"
role_password_identity="$(ensure_generated_secret jobtrack-role-password-identity)"
role_password_pat_management="$(ensure_generated_secret jobtrack-role-password-pat-management)"
role_password_pat_authentication="$(ensure_generated_secret jobtrack-role-password-pat-authentication)"
# Not one of the four application connection strings -- no running service ever holds this one. It
# exists only so an operator can recover a locked or otherwise inaccessible account later via
# docker/emergency-reset.sh, run by the transient least-privilege helper job. See
# docs/operations/postgresql-cloud-run-deployment.md §"Recovering a locked or
# inaccessible account".
role_password_emergency_reset="$(ensure_generated_secret jobtrack-role-password-emergency-reset)"
admin_password="$(ensure_generated_secret jobtrack-account-password-admin)"
user1_password="$(ensure_generated_secret jobtrack-account-password-user1)"
user2_password="$(ensure_generated_secret jobtrack-account-password-user2)"
data_protection_certificate_password=
ensure_data_protection_material

db_admin_password_version="$(secret_version jobtrack-db-admin-password)"
role_password_domain_version="$(secret_version jobtrack-role-password-domain)"
role_password_identity_version="$(secret_version jobtrack-role-password-identity)"
role_password_pat_management_version="$(secret_version jobtrack-role-password-pat-management)"
role_password_pat_authentication_version="$(secret_version jobtrack-role-password-pat-authentication)"
role_password_emergency_reset_version="$(secret_version jobtrack-role-password-emergency-reset)"
admin_password_version="$(secret_version jobtrack-account-password-admin)"
user1_password_version="$(secret_version jobtrack-account-password-user1)"
user2_password_version="$(secret_version jobtrack-account-password-user2)"
data_protection_certificate_version="$(secret_version jobtrack-data-protection-certificate)"
data_protection_certificate_password_version="$(secret_version jobtrack-data-protection-certificate-password)"

# ---- Cloud SQL --------------------------------------------------------------

if gcloud sql instances describe "$sql_instance" --project="$project" >/dev/null 2>&1; then
	echo "==> Cloud SQL instance $sql_instance already exists"
else
	echo "==> creating Cloud SQL instance $sql_instance ($sql_version, $sql_tier) -- this takes several minutes"
	printf -- '--root-password: %s\n' "$db_admin_password" >"$database_password_flags"
	gcloud sql instances create "$sql_instance" \
		--project="$project" \
		--region="$region" \
		--database-version="$sql_version" \
		--edition=ENTERPRISE \
		--tier="$sql_tier" \
		--storage-auto-increase \
		--backup-start-time="$sql_backup_start_time" \
		--enable-point-in-time-recovery \
		--connector-enforcement=REQUIRED \
		--deletion-protection \
		--retain-backups-on-delete \
		--final-backup \
		--final-backup-retention-days=30 \
		--enable-password-policy \
		--password-policy-min-length=20 \
		--password-policy-disallow-username-substring \
		--ssl-mode=ENCRYPTED_ONLY \
		--flags-file="$database_password_flags" \
		--quiet
fi

# Applied unconditionally so an instance created by an earlier run is brought up to the same posture.
# Authorized networks are cleared and connector enforcement is required, so every connection must
# arrive through an authenticated Cloud SQL connector. The remaining SSL mode is defence in depth.
gcloud sql instances patch "$sql_instance" \
	--project="$project" \
	--ssl-mode=ENCRYPTED_ONLY \
	--connector-enforcement=REQUIRED \
	--clear-authorized-networks \
	--deletion-protection \
	--retain-backups-on-delete \
	--final-backup \
	--final-backup-retention-days=30 \
	--storage-auto-increase \
	--backup-start-time="$sql_backup_start_time" \
	--retained-backups-count=7 \
	--retained-transaction-log-days=7 \
	--enable-point-in-time-recovery \
	--enable-password-policy \
	--password-policy-min-length=20 \
	--password-policy-disallow-username-substring \
	--quiet >/dev/null

# Unconditional, so the instance's password always matches the secret even if the two were created in
# separate runs (or the secret was recreated after a partial teardown).
printf -- '--password: %s\n' "$db_admin_password" >"$database_password_flags"
gcloud sql users set-password postgres \
	--instance="$sql_instance" --project="$project" --flags-file="$database_password_flags" --quiet

if gcloud sql databases describe "$sql_database" --instance="$sql_instance" --project="$project" >/dev/null 2>&1; then
	echo "==> database $sql_database already exists"
else
	# Default UTF-8 encoding and collation. The schema declares no COLLATE clauses and the application
	# compares and formats through CultureInfo.InvariantCulture exclusively, so no ICU locale is pinned
	# here (unlike the hand-provisioned instance in production-deployment.md, which sets one for
	# operator-facing consistency rather than application correctness).
	echo "==> creating database $sql_database"
	gcloud sql databases create "$sql_database" --instance="$sql_instance" --project="$project" --quiet
fi

instance_connection_name="$project:$region:$sql_instance"
# The Cloud SQL connector mounts a Unix socket directory at this path in both the service and the job.
# PostgreSqlTransportSecurity.Validate exempts Unix-socket connections from its remote-host
# SSL Mode=VerifyFull requirement -- honestly, since the traffic never leaves the instance.
db_host="/cloudsql/$instance_connection_name"

connection_string() {
	printf 'Host=%s;Database=%s;Username=%s;Password=%s' "$db_host" "$sql_database" "$1" "$2"
}

echo "==> storing the four application connection strings"
put_secret jobtrack-cs-domain "$(connection_string jobtrack_domain_login "$role_password_domain")"
put_secret jobtrack-cs-identity "$(connection_string jobtrack_identity_login "$role_password_identity")"
put_secret jobtrack-cs-pat-management "$(connection_string jobtrack_pat_management_login "$role_password_pat_management")"
put_secret jobtrack-cs-pat-authentication "$(connection_string jobtrack_pat_authentication_login "$role_password_pat_authentication")"

cs_domain_version="$(secret_version jobtrack-cs-domain)"
cs_identity_version="$(secret_version jobtrack-cs-identity)"
cs_pat_management_version="$(secret_version jobtrack-cs-pat-management)"
cs_pat_authentication_version="$(secret_version jobtrack-cs-pat-authentication)"

# ---- data-protection key ring ----------------------------------------------
# The key ring must outlive the container: lose it and every session cookie and antiforgery token is
# invalidated, silently signing every user out. A handful of rarely written XML files is well inside
# what Cloud Storage FUSE handles -- unlike a database's write pattern, which is why the database is
# a managed instance and not a volume (see the operations doc).
if gcloud storage buckets describe "gs://$key_bucket" --project="$project" >/dev/null 2>&1; then
	echo "==> key-ring bucket gs://$key_bucket already exists"
else
	echo "==> creating key-ring bucket gs://$key_bucket"
	# --public-access-prevention forecloses the classic "someone made the bucket public" failure
	# outright, rather than relying on nobody ever adding allUsers. Uniform access removes per-object
	# ACLs as a second, easily-missed way to grant it. Versioning is recovery, not security: an
	# overwritten or truncated key ring signs every user out, and a previous version restores it.
	gcloud storage buckets create "gs://$key_bucket" \
		--project="$project" --location="$region" \
		--uniform-bucket-level-access --public-access-prevention --quiet
fi

# Reconcile existing buckets too: creation-time flags alone do not repair later configuration drift.
gcloud storage buckets update "gs://$key_bucket" \
	--public-access-prevention \
	--uniform-bucket-level-access \
	--versioning \
	--soft-delete-duration=30d \
	--quiet >/dev/null

# ---- IAM --------------------------------------------------------------------

echo "==> granting each service account only what it needs"

# The runtime needs to open the Cloud SQL socket. roles/cloudsql.client grants
# connect-and-authenticate only: it is not a database privilege, so what the identity can actually do
# inside the database is still decided by the PostgreSQL role its connection string authenticates as.
# The provisioning identity receives the same role later, with a hard expiry, immediately around the
# transient job execution.
gcloud projects add-iam-policy-binding "$project" \
	--member="serviceAccount:$run_service_account" \
	--role=roles/cloudsql.client --condition=None --quiet >/dev/null

grant_secret_access() {
	local account=$1
	shift
	for secret in "$@"; do
		gcloud secrets add-iam-policy-binding "$secret" \
			--project="$project" \
			--member="serviceAccount:$account" \
			--role=roles/secretmanager.secretAccessor --condition=None --quiet >/dev/null
	done
}

# The service gets the four application connection strings and nothing else. It cannot read the
# database admin password, so a compromise of the running app cannot escalate to the PostgreSQL
# superuser, and it cannot read the three account passwords either.
grant_secret_access "$run_service_account" \
	jobtrack-cs-domain jobtrack-cs-identity jobtrack-cs-pat-management jobtrack-cs-pat-authentication \
	jobtrack-data-protection-certificate jobtrack-data-protection-certificate-password

# Remove grants left by the earlier standing-job design. The emergency helper grants these only for
# the duration of a recovery execution, then revokes them in its EXIT trap.
gcloud secrets remove-iam-policy-binding jobtrack-role-password-emergency-reset \
	--project="$project" \
	--member="serviceAccount:$emergency_service_account" \
	--role=roles/secretmanager.secretAccessor --condition=None --quiet >/dev/null 2>&1 || true
gcloud projects remove-iam-policy-binding "$project" \
	--member="serviceAccount:$emergency_service_account" \
	--role=roles/cloudsql.client --condition=None --quiet >/dev/null 2>&1 || true

# Only the service writes the key ring; the provisioning job has no business reading it.
gcloud storage buckets add-iam-policy-binding "gs://$key_bucket" \
	--member="serviceAccount:$run_service_account" \
	--role=roles/storage.objectUser --quiet >/dev/null
# Remove the superseded broader grant from deployments created by an earlier version of this script.
gcloud storage buckets remove-iam-policy-binding "gs://$key_bucket" \
	--member="serviceAccount:$run_service_account" \
	--role=roles/storage.objectAdmin --quiet >/dev/null 2>&1 || true

# ---- build and push ---------------------------------------------------------
# --platform linux/amd64 matters on Apple Silicon: Cloud Run runs amd64 and the local daemon defaults
# to the host's arm64. The build context is the monorepo root, not JobTrack/ -- see the Dockerfile's
# header comment.

if gcloud artifacts repositories describe "$repository" --location="$region" --project="$project" >/dev/null 2>&1; then
	echo "==> Artifact Registry repository $repository already exists"
else
	echo "==> creating Artifact Registry repository $repository"
	gcloud artifacts repositories create "$repository" \
		--repository-format=docker --location="$region" --project="$project" \
		--immutable-tags --allow-vulnerability-scanning --quiet
fi

# These controls are reconciled for a repository created by an older script as well.
gcloud artifacts repositories update "$repository" \
	--location="$region" --project="$project" \
	--immutable-tags --allow-vulnerability-scanning --quiet >/dev/null

echo "==> configuring Docker authentication for Artifact Registry"
gcloud auth configure-docker "$region-docker.pkg.dev" --project="$project" --quiet

echo "==> building and pushing $provision_image (provisioning target: shell, psql, AdminCli)"
docker buildx build -f "$repo/Dockerfile.postgresql" --target provision \
	-t "$provision_image" --platform linux/amd64 --sbom=true --provenance=mode=max \
	--build-arg="SOURCE_REVISION_ID=$source_revision_id" \
	--label="org.opencontainers.image.revision=$source_revision" --push "$monorepo_root"

echo "==> building and pushing $serve_image (serve target: chiseled, web only)"
docker buildx build -f "$repo/Dockerfile.postgresql" \
	-t "$serve_image" --platform linux/amd64 --sbom=true --provenance=mode=max \
	--build-arg="SOURCE_REVISION_ID=$source_revision_id" \
	--label="org.opencontainers.image.revision=$source_revision" --push "$monorepo_root"

provision_digest="$(gcloud artifacts docker images describe "$provision_image" --format='value(image_summary.digest)')"
serve_digest="$(gcloud artifacts docker images describe "$serve_image" --format='value(image_summary.digest)')"
provision_image_by_digest="${provision_image%:*}@$provision_digest"
serve_image_by_digest="${serve_image%:*}@$serve_digest"

scan_image_for_release() {
	local image=$1 scan_name vulnerabilities blocking_count
	echo "==> scanning $image"
	# --quiet: on first use gcloud prompts to install its bundled Python runtime, which an unattended
	# deploy cannot answer. Accepting the default is what an interactive operator would do anyway.
	# `scan` returns a long-running operation, whose `name` is the operation itself; the scan resource
	# list-vulnerabilities takes is in `response.scan`. Reading `name` 404s the severity gate below.
	scan_name="$(gcloud artifacts docker images scan "$image" --quiet \
		--project="$project" --remote --location=europe --format='value(response.scan)')"
	if [[ -z $scan_name ]]; then
		echo "ERROR: Artifact Analysis returned no scan identifier for $image." >&2
		return 1
	fi

	vulnerabilities="$(gcloud artifacts docker images list-vulnerabilities "$scan_name" --quiet \
		--project="$project" --location=europe --format=json)"
	blocking_count="$(jq '[.[] | select(
		.vulnerability.effectiveSeverity == "CRITICAL" or .vulnerability.effectiveSeverity == "HIGH")] | length' <<<"$vulnerabilities")"
	if [[ $blocking_count != 0 ]]; then
		echo "ERROR: $image has $blocking_count HIGH/CRITICAL vulnerabilities." >&2
		jq -r '.[] | select(
			.vulnerability.effectiveSeverity == "CRITICAL" or .vulnerability.effectiveSeverity == "HIGH")
			| [.vulnerability.effectiveSeverity, .vulnerability.shortDescription, .packageIssue[0].affectedPackage] | @tsv' \
			<<<"$vulnerabilities" >&2
		return 1
	fi
}

scan_image_for_release "$provision_image_by_digest"
scan_image_for_release "$serve_image_by_digest"

# ---- release authorization --------------------------------------------------
# BuildKit's SBOM and provenance attestations are evidence, but they do not themselves stop an
# operator from deploying a different digest. The one-time setup script creates a KMS-backed
# attestor and a fail-closed project policy. This release attestation means both images passed the
# vulnerability gate above; Cloud Run verifies it before accepting either revision or job.

if ! gcloud container binauthz attestors describe "$release_attestor" \
	--project="$project" >/dev/null 2>&1; then
	echo "ERROR: Binary Authorization is not configured for this project." >&2
	echo "Run: ./scripts/configure-cloudrun-binary-authorization.sh $project $region" >&2
	exit 1
fi

binary_authorization_policy="$(gcloud container binauthz policy export --project="$project" --format=json)"
if ! jq -e --arg attestor "projects/$project/attestors/$release_attestor" '
	.defaultAdmissionRule.evaluationMode == "REQUIRE_ATTESTATION" and
	.defaultAdmissionRule.enforcementMode == "ENFORCED_BLOCK_AND_AUDIT_LOG" and
	(.defaultAdmissionRule.requireAttestationsBy | index($attestor) != null)' \
	<<<"$binary_authorization_policy" >/dev/null; then
	echo "ERROR: the project Binary Authorization policy does not require the JobTrack release attestor." >&2
	echo "Run: ./scripts/configure-cloudrun-binary-authorization.sh $project $region" >&2
	exit 1
fi

if ! gcloud kms keys versions describe "$release_key_version" \
	--key="$release_key" --keyring="$release_keyring" --location="$region" \
	--project="$project" >/dev/null 2>&1; then
	echo "ERROR: the JobTrack Binary Authorization signing key is missing." >&2
	echo "Run: ./scripts/configure-cloudrun-binary-authorization.sh $project $region" >&2
	exit 1
fi

attestation_index=0
attest_digest() {
	local image=$1 payload signature public_key_id creation_error
	attestation_index=$((attestation_index + 1))
	payload="$temporary_directory/attestation-$attestation_index.json"
	signature="$temporary_directory/attestation-$attestation_index.sig"
	creation_error="$temporary_directory/attestation-$attestation_index.err"

	echo "==> signing release attestation for $image"
	gcloud container binauthz create-signature-payload \
		--artifact-url="$image" >"$payload"
	gcloud kms asymmetric-sign \
		--project="$project" \
		--location="$region" \
		--keyring="$release_keyring" \
		--key="$release_key" \
		--version="$release_key_version" \
		--digest-algorithm=sha256 \
		--input-file="$payload" \
		--signature-file="$signature" \
		--quiet
	public_key_id="$(gcloud container binauthz attestors describe "$release_attestor" \
		--project="$project" --format='value(userOwnedGrafeasNote.publicKeys[0].id)')"

	# An identical digest may already carry this release's attestation when a rebuild reproduces it
	# byte for byte; that is success, not a conflict. Any other failure still stops the release.
	if ! gcloud container binauthz attestations create \
		--project="$project" \
		--artifact-url="$image" \
		--attestor="$release_attestor" \
		--attestor-project="$project" \
		--payload-file="$payload" \
		--signature-file="$signature" \
		--public-key-id="$public_key_id" \
		--validate \
		--quiet 2>"$creation_error"; then
		if ! grep -qi 'already exists' "$creation_error"; then
			cat "$creation_error" >&2
			return 1
		fi
	fi
}

# Building with --sbom/--provenance publishes an OCI image *index*, not a single manifest: the
# platform image plus a BuildKit attestation manifest describing it. Cloud Run resolves that index to
# the platform-specific child and Binary Authorization evaluates the *child's* digest, so signing only
# the index digest is rejected with "No attestations found that were valid and signed by a key trusted
# by the attestor". Sign the index and every real child image; the attestation manifests themselves
# are never deployed and are skipped.
attest_image_for_release() {
	local image=$1 repository child
	repository="${image%@*}"

	attest_digest "$image"

	while read -r child; do
		[[ -n $child ]] || continue
		attest_digest "$repository@$child"
	done < <(docker buildx imagetools inspect "$image" --raw |
		jq -r '.manifests[]?
			| select((.annotations["vnd.docker.reference.type"] // "") != "attestation-manifest")
			| .digest')
}

attest_image_for_release "$provision_image_by_digest"
attest_image_for_release "$serve_image_by_digest"

# ---- candidate service ------------------------------------------------------
# ForwardedHeaders__KnownNetworks__0=0.0.0.0/0: Program.cs requires a trusted-proxy entry outside
# Development to accept X-Forwarded-Proto, and trusting any source is reasonable specifically because
# Cloud Run does not allow direct public access to the container -- only Google's own front end can
# ever set that header. AllowedHosts likewise has to be set (Program.cs rejects an unset or '*'
# value); the project-number hostname is known before the first deploy, so even the bootstrap
# revision is restricted to this service rather than the global *.run.app suffix.
#
# --max-instances=1 IS NOT A COST SETTING. Four in-process stores (remembered page filters, the login
# rate limiter, pending PAT delivery, the external API rate limiter) are not shared between
# instances, and under two they fail silently rather than refusing to start -- see
# docs/operations/production-deployment.md §"In-process state that breaks under a second web
# instance". Raising this is a code change first.
#
# A schema change and a Cloud Run traffic update cannot be one transaction. The deployment therefore
# uses the expand/contract rolling-release contract: first prove the new digest can start with no
# traffic, apply only backward-compatible schema changes while the old revision serves, exercise a
# fresh candidate against that schema, then promote it explicitly. A failed candidate never receives
# production traffic. Destructive/contracting schema changes require a later release after every old
# revision has been retired.

project_number="$(gcloud projects describe "$project" --format='value(projectNumber)')"
alternate_service_host="$service-$project_number.$region.run.app"
candidate_tag="candidate-$build_nonce"
candidate_smoke_path="/Account/Login"
candidate_smoke_attempts=6
candidate_smoke_timeout_seconds=20

existing_service_host=
if gcloud run services describe "$service" --project="$project" --region="$region" >/dev/null 2>&1; then
	existing_url="$(gcloud run services describe "$service" \
		--project="$project" --region="$region" --format='value(status.url)')"
	existing_service_host="${existing_url#https://}"
fi

deploy_candidate() {
	local allowed_hosts=$1 expose_tag=$2
	local -a routing_flags=(--no-traffic)
	if [[ $expose_tag == true ]]; then
		routing_flags+=(--tag="$candidate_tag")
	fi

	gcloud run deploy "$service" \
		--project="$project" \
		--region="$region" \
		--image="$serve_image_by_digest" \
		--service-account="$run_service_account" \
		--port=8080 \
		--allow-unauthenticated \
		--ingress=all \
		--min-instances=0 \
		--max-instances=1 \
		--set-cloudsql-instances="$instance_connection_name" \
		--add-volume="name=$key_volume,type=cloud-storage,bucket=$key_bucket" \
		--add-volume-mount="volume=$key_volume,mount-path=$key_mount_path" \
		--set-env-vars="^@^ForwardedHeaders__KnownNetworks__0=0.0.0.0/0@AllowedHosts=$allowed_hosts" \
		--set-secrets="ConnectionStrings__JobTrackDomain=jobtrack-cs-domain:$cs_domain_version,ConnectionStrings__JobTrackIdentity=jobtrack-cs-identity:$cs_identity_version,ConnectionStrings__JobTrackPatManagement=jobtrack-cs-pat-management:$cs_pat_management_version,ConnectionStrings__JobTrackPatAuthentication=jobtrack-cs-pat-authentication:$cs_pat_authentication_version,$certificate_mount_path=jobtrack-data-protection-certificate:$data_protection_certificate_version,$certificate_password_mount_path=jobtrack-data-protection-certificate-password:$data_protection_certificate_password_version" \
		--binary-authorization=default \
		"${routing_flags[@]}" \
		--quiet
}

# This first revision carries no tag and takes no traffic, so it needs no candidate hostname: it only
# has to prove the digest and its configuration can start.
preliminary_allowed_hosts="$alternate_service_host"
if [[ -n $existing_service_host ]]; then
	preliminary_allowed_hosts="$preliminary_allowed_hosts;$existing_service_host"
fi

echo "==> staging the no-traffic candidate before the schema upgrade"
deploy_candidate "$preliminary_allowed_hosts" false

# ---- provisioning job -------------------------------------------------------
# Schema, five login roles, and the three accounts. Every step is skipped if already done, so this
# also applies new schema versions to an existing database. The 30-minute IAM condition outlives the
# 15-minute job deadline but expires independently even if this process is killed before its EXIT
# trap can revoke the grants. --max-retries=0 surfaces a failure instead of retrying against a
# partially provisioned database.

provision_access_duration_minutes=30
if provision_access_expiry="$(date -u -v+"${provision_access_duration_minutes}"M '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null)"; then
	:
else
	provision_access_expiry="$(date -u -d "+$provision_access_duration_minutes minutes" '+%Y-%m-%dT%H:%M:%SZ')"
fi
provision_access_condition="title=jobtrack-provision-$build_nonce,description=Temporary JobTrack schema deployment access,expression=request.time < timestamp(\"$provision_access_expiry\")"

grant_provision_access() {
	provision_access_may_exist=true
	gcloud projects add-iam-policy-binding "$project" \
		--member="serviceAccount:$provision_service_account" \
		--role=roles/cloudsql.client \
		--condition="$provision_access_condition" --quiet >/dev/null
	for secret in \
		jobtrack-db-admin-password \
		jobtrack-role-password-domain jobtrack-role-password-identity \
		jobtrack-role-password-pat-management jobtrack-role-password-pat-authentication \
		jobtrack-role-password-emergency-reset \
		jobtrack-account-password-admin jobtrack-account-password-user1 jobtrack-account-password-user2; do
		gcloud secrets add-iam-policy-binding "$secret" \
			--project="$project" \
			--member="serviceAccount:$provision_service_account" \
			--role=roles/secretmanager.secretAccessor \
			--condition="$provision_access_condition" --quiet >/dev/null
	done
}

echo "==> granting expiring provisioning access and running the schema job"
grant_provision_access
provision_job_may_exist=true
gcloud run jobs deploy "$provision_job" \
	--project="$project" \
	--region="$region" \
	--image="$provision_image_by_digest" \
	--service-account="$provision_service_account" \
	--set-cloudsql-instances="$instance_connection_name" \
	--binary-authorization=default \
	--max-retries=0 \
	--task-timeout=15m \
	--set-env-vars="^@^JOBTRACK_DB_HOST=$db_host@JOBTRACK_DB_NAME=$sql_database@JOBTRACK_DB_ADMIN_USER=postgres@JOBTRACK_ADMIN_USERNAME=$admin_username@JOBTRACK_ADMIN_DISPLAY_NAME=$admin_display_name@JOBTRACK_USER1_USERNAME=$user1_username@JOBTRACK_USER1_DISPLAY_NAME=$user1_display_name@JOBTRACK_USER1_ROLES=$user1_roles@JOBTRACK_USER2_USERNAME=$user2_username@JOBTRACK_USER2_DISPLAY_NAME=$user2_display_name@JOBTRACK_USER2_ROLES=$user2_roles@JOBTRACK_TIME_ZONE=$time_zone" \
	--set-secrets="JOBTRACK_DB_ADMIN_PASSWORD=jobtrack-db-admin-password:$db_admin_password_version,JOBTRACK_ROLE_PASSWORD_DOMAIN=jobtrack-role-password-domain:$role_password_domain_version,JOBTRACK_ROLE_PASSWORD_IDENTITY=jobtrack-role-password-identity:$role_password_identity_version,JOBTRACK_ROLE_PASSWORD_PAT_MANAGEMENT=jobtrack-role-password-pat-management:$role_password_pat_management_version,JOBTRACK_ROLE_PASSWORD_PAT_AUTHENTICATION=jobtrack-role-password-pat-authentication:$role_password_pat_authentication_version,JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET=jobtrack-role-password-emergency-reset:$role_password_emergency_reset_version,JOBTRACK_ADMIN_PASSWORD=jobtrack-account-password-admin:$admin_password_version,JOBTRACK_USER1_PASSWORD=jobtrack-account-password-user1:$user1_password_version,JOBTRACK_USER2_PASSWORD=jobtrack-account-password-user2:$user2_password_version" \
	--execute-now --wait --quiet
delete_provision_job
revoke_provision_access

# ---- candidate validation and promotion ------------------------------------

service_url="$(gcloud run services describe "$service" \
	--project="$project" --region="$region" --format='value(status.url)')"
service_host="${service_url#https://}"

# Cloud Run publishes a tagged revision at <tag>---<status.url host>, built on the legacy
# <service>-<hash>-<regioncode>.a.run.app name rather than the <service>-<project-number> one. The
# smoke test below requests whatever URL the API reports, so this must be derived from service_host:
# building it from alternate_service_host names a host the app's filter rejects with 400, failing
# every deployment after the schema has already been applied.
candidate_host="$candidate_tag---$service_host"
allowed_hosts="$candidate_host;$alternate_service_host;$service_host"

# Create a fresh revision after provisioning. This is the candidate actually tested and promoted;
# the earlier revision proves only that image/configuration startup is independent of the migration.
echo "==> staging the candidate against the upgraded schema"
deploy_candidate "$allowed_hosts" true

candidate_url="$(gcloud run services describe "$service" --project="$project" --region="$region" --format=json |
	jq -r --arg tag "$candidate_tag" '.status.traffic[] | select(.tag == $tag) | .url')"
if [[ -z $candidate_url ]]; then
	echo "ERROR: Cloud Run did not publish a URL for candidate tag $candidate_tag." >&2
	exit 1
fi

smoke_test_candidate() {
	echo "==> smoke-testing $candidate_url$candidate_smoke_path"
	curl --fail --silent --show-error \
		--retry "$candidate_smoke_attempts" \
		--retry-all-errors \
		--max-time "$candidate_smoke_timeout_seconds" \
		"$candidate_url$candidate_smoke_path" >/dev/null
}

smoke_test_candidate

echo "==> promoting the validated candidate to 100% traffic"
gcloud run services update-traffic "$service" \
	--project="$project" --region="$region" \
	--to-tags="$candidate_tag=100" --quiet >/dev/null
gcloud run services update-traffic "$service" \
	--project="$project" --region="$region" \
	--remove-tags="$candidate_tag" --quiet >/dev/null

url="https://$alternate_service_host"

echo
echo "==> deployed: $url"
echo "==> database: Cloud SQL $instance_connection_name (persistent -- survives every recycle and redeploy)"
echo "==> no example job nodes were installed; the tree below the root node is empty"
echo
echo "    Three accounts were created. EACH PASSWORD MUST BE CHANGED ON FIRST SIGN-IN (ADR 0023)."
echo "    Retrieve each one-time enrolment credential directly from Secret Manager only when handing"
echo "    it to its intended user:"
echo
printf '      %-10s %-22s %s\n' "USERNAME" "SECRET" "ROLES"
printf '      %-10s %-22s %s\n' "$admin_username" "jobtrack-account-password-admin" "Administrator"
printf '      %-10s %-22s %s\n' "$user1_username" "jobtrack-account-password-user1" "$user1_roles"
printf '      %-10s %-22s %s\n' "$user2_username" "jobtrack-account-password-user2" "$user2_roles"
echo
echo "    Example: gcloud secrets versions access latest --secret=jobtrack-account-password-admin --project=$project"
echo
echo "Tear down when done -- the Cloud SQL instance bills continuously and does not scale to zero:"
echo "  gcloud run services delete $service --project=$project --region=$region --quiet"
echo "  gcloud run jobs delete $provision_job --project=$project --region=$region --quiet"
echo "  gcloud sql instances delete $sql_instance --project=$project --quiet"
echo "  gcloud storage rm -r gs://$key_bucket"
