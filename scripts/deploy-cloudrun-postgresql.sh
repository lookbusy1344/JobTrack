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
# Idempotent and re-runnable. Every resource is created only if absent, and secrets keep their
# existing values unless JOBTRACK_ROTATE_DATABASE_CREDENTIALS=true explicitly requests a coordinated
# database credential rotation. A second run applies new schema versions and redeploys the current
# build without locking you out.
#
# Usage: ./scripts/deploy-cloudrun-postgresql.sh <gcp-project-id> [region]
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
monorepo_root="$(cd "$repo/.." && pwd)"

project="${1:?Usage: $0 <gcp-project-id> [region]}"
# Every gcloud call below also passes --project explicitly, but this is the backstop that makes the
# argument authoritative: a call that forgets the flag would otherwise silently resolve against
# whatever `gcloud config set project` the operator last ran -- a different project entirely, which is
# how a deploy of *this* project once went looking for its images in the SQLite demo's registry. The
# target is an argument to this script, so it must not be inferable from ambient machine state.
export CLOUDSDK_CORE_PROJECT="$project"
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
# Separate directories, not two files in one: Cloud Run backs each secret file mount with its own
# directory volume and refuses a second, different secret mounted into a directory already in use.
certificate_mount_path="/var/run/secrets/jobtrack/certificate/data-protection.pfx"
certificate_password_mount_path="/var/run/secrets/jobtrack/certificate-password/data-protection-password"
orbstack_socket="${HOME}/.orbstack/run/docker.sock"
# Named explicitly and passed to every build rather than inheriting whichever builder happens to be
# selected: the default `docker` driver cannot produce the SBOM/provenance attestations below, failing
# with "Attestation is not supported for the docker driver" partway through a deployment that has
# already patched Cloud SQL. Everything else here is pinned (image digests, secret versions, immutable
# tags); the builder was the one input still taken from ambient machine state.
buildx_builder="jobtrack-builder"
buildx_required_driver="docker-container"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/jobtrack-cloudrun-deploy.XXXXXX")"
database_password_flags="$temporary_directory/database-password-flags.yaml"
provision_job_may_exist=false
provision_access_may_exist=false
provision_access_condition=
# Every temporary provisioning grant is titled with this prefix plus the run's own nonce, so a grant
# can be recognised as JobTrack's -- and as belonging to some *other* run -- from the policy alone.
provision_access_title_prefix="jobtrack-provision-"
rotate_database_credentials="${JOBTRACK_ROTATE_DATABASE_CREDENTIALS:-false}"
if [[ $rotate_database_credentials != true && $rotate_database_credentials != false ]]; then
	echo "ERROR: JOBTRACK_ROTATE_DATABASE_CREDENTIALS must be 'true' or 'false' when set." >&2
	exit 2
fi

# The secrets the provisioning job reads. One list, used by both the grant and the two revoke paths,
# so they cannot drift apart and strand a grant nothing removes.
provision_secrets=(
	jobtrack-db-admin-password
	jobtrack-role-password-domain
	jobtrack-role-password-history-deletion
	jobtrack-role-password-credential-administration
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

# Set only by the last line of the script. Cleanup treats "exited without reaching the end" as failure
# even when the shell reports success, because bash 3.2 -- the macOS system bash this runs under --
# resets $? to 0 before the EXIT trap when `set -u` aborts on an unbound variable. A run killed that
# way otherwise reports success while having deployed nothing, which is the one outcome a deployment
# script must never produce. Ordinary failures already propagate their own status and keep it.
deployment_completed=false

cleanup() {
	local status=$?
	delete_provision_job
	revoke_provision_access
	rm -rf "$temporary_directory"

	if [[ $deployment_completed != true && $status -eq 0 ]]; then
		echo "ERROR: the deployment exited before completing, but reported success; treating it as failed." >&2
		status=1
	fi

	exit "$status"
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

# db-custom-1-3840 (1 vCPU, 3.75 GiB, dedicated core) replaces the earlier db-f1-micro. Unlike the
# Cloud Run service, a Cloud SQL instance does not scale to zero -- it bills continuously from
# creation until deletion; this tier costs more than the free-tier-eligible db-f1-micro, chosen
# specifically to resolve ADR 0066 §11's connection-budget gap (see the multi-instance capacity policy
# below) rather than for any measured throughput need.
sql_tier="db-custom-1-3840"
sql_version="POSTGRES_18"
sql_backup_start_time="03:00"
# A live instance is regional rather than zonal: Cloud SQL maintains a synchronously replicated
# standby in another zone and fails over without changing the connection name. The extra instance
# cost is intentional; a zonal deployment is retained only by the separate development topologies.
sql_availability_type="regional"

# ---- multi-instance capacity policy (ADR 0066 §10-11, Stage 8 items 1 and 3) -----------------------
# Service-level min/max instances, revision concurrency, and the per-pool connection budget are the
# knobs ADR 0066 explicitly leaves as implementation-time decisions -- see ADR 0066 §10 for the
# instance policy and §11 for the connection-budget formula this block implements.
min_instances=0
max_instances=2
container_concurrency=80

# The service-level maximum is divided across traffic-serving revisions, so rolling overlap remains
# inside max_instances. A tagged no-traffic candidate is started outside that allocation, however,
# and Cloud Run may briefly exceed the configured service maximum. Budget for both explicitly.
overshoot_hosts=1
tagged_candidate_hosts=1
planned_peak_hosts=$((max_instances + overshoot_hosts + tagged_candidate_hosts))

# Cloud SQL for PostgreSQL sizes max_connections from instance memory; the documented figure for this
# tier's 3.75 GiB bracket is 100. This is a documentation lookup, not something gcloud reports for a
# machine type, and this script cannot reach the private instance to confirm it -- so it is passed to
# the provisioning job as JOBTRACK_EXPECTED_MAX_CONNECTIONS and checked there against the server's own
# `SHOW max_connections`, which fails the deployment if the real ceiling is smaller.
database_max_connections=100
# Reserved for the Cloud SQL admin/superuser connection, an operator's own psql session, and the
# transient provisioning/emergency jobs -- never assumed available to the six application pools.
operator_and_deployment_reserve=10
usable_database_connections=$((database_max_connections - operator_and_deployment_reserve))
host_budget=$((usable_database_connections / planned_peak_hosts))

# A weighted, not equal, split of one host's budget across its six distinct connection-string pools:
# the domain pool carries the bulk of Razor Pages and external-API traffic, Identity serves
# authentication reads on every request, and the two PAT pools are comparatively low-volume.
domain_pool_max_size=7
history_deletion_pool_max_size=2
credential_administration_pool_max_size=3
identity_pool_max_size=6
pat_management_pool_max_size=2
pat_authentication_pool_max_size=2
pool_budget_total=$((domain_pool_max_size + history_deletion_pool_max_size + credential_administration_pool_max_size + identity_pool_max_size + pat_management_pool_max_size + pat_authentication_pool_max_size))
if ((pool_budget_total > host_budget)); then
	echo "ERROR: planned per-host pool budget ($pool_budget_total) exceeds the calculated host budget ($host_budget)." >&2
	echo "Reduce a Maximum Pool Size value, raise the Cloud SQL tier, or lower max_instances." >&2
	exit 1
fi

# Cloud Run never deletes a revision on its own, and every deploy creates two (a no-traffic
# pre-migration candidate, then the promoted post-migration one). Left alone they accumulate
# indefinitely; scale-to-zero means the idle ones cost nothing to run, but the image layers backing
# them still sit in Artifact Registry. Keep only the most recent few for rollback.
revision_keep_count=3

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

# Resolved before anything is created or patched. The build is minutes away, but a missing
# attestation-capable builder is a local-machine fault that should stop the run while it is still a
# no-op -- not after Cloud SQL has been patched and secrets rewritten.
ensure_buildx_builder() {
	local driver
	if ! docker buildx inspect "$buildx_builder" >/dev/null 2>&1; then
		echo "==> creating the '$buildx_builder' buildx builder ($buildx_required_driver driver, required for attestations)"
		docker buildx create --name "$buildx_builder" --driver "$buildx_required_driver" >/dev/null
		return 0
	fi

	# `docker buildx inspect --format` is not available on every supported buildx version, so read the
	# driver from the plain output instead.
	driver="$(docker buildx inspect "$buildx_builder" 2>/dev/null |
		awk '/^Driver:/ { print $2; exit }')"
	if [[ $driver != "$buildx_required_driver" ]]; then
		echo "ERROR: buildx builder '$buildx_builder' uses the '$driver' driver, which cannot produce SBOM/provenance attestations." >&2
		echo "Remove it and rerun so this script can recreate it: docker buildx rm $buildx_builder" >&2
		exit 1
	fi
}

ensure_buildx_builder

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
	logging.googleapis.com \
	monitoring.googleapis.com \
	--project="$project" --quiet

echo "==> reconciling audit logging and production alerts"
"$here/configure-cloudrun-monitoring.sh" "$project" "$region"

echo "==> removing default-project residue unused by this dedicated topology"
"$here/harden-cloudrun-postgresql-project.sh" "$project"

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
sql_instance_preexists=false
if gcloud sql instances describe "$sql_instance" --project="$project" >/dev/null 2>&1; then
	sql_instance_preexists=true
fi

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

ensure_database_secret() {
	local name=$1 value
	if [[ $rotate_database_credentials != true ]] || ! secret_exists "$name"; then
		ensure_generated_secret "$name"
		return 0
	fi

	value="$(generate_password)"
	printf '%s' "$value" | gcloud secrets versions add "$name" \
		--project="$project" --data-file=- >/dev/null
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

# Account passwords are one-time enrolment material, not standing deployment credentials. A fresh
# instance creates them; an existing instance uses an enabled version if one remains, but never
# silently recreates a secret an operator retired after confirming the password was changed. The
# provisioning job itself checks the database and fails closed if an account is unexpectedly absent.
ensure_enrolment_secret() {
	local name=$1 version
	if ! secret_exists "$name"; then
		if [[ $sql_instance_preexists == true ]]; then
			return 0
		fi
		ensure_generated_secret "$name"
		return 0
	fi

	version="$(secret_version "$name")"
	if [[ -n $version ]]; then
		gcloud secrets versions access "$version" --secret="$name" --project="$project"
	fi
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
db_admin_password="$(ensure_database_secret jobtrack-db-admin-password)"
role_password_domain="$(ensure_database_secret jobtrack-role-password-domain)"
role_password_history_deletion="$(ensure_database_secret jobtrack-role-password-history-deletion)"
role_password_credential_administration="$(ensure_database_secret jobtrack-role-password-credential-administration)"
role_password_identity="$(ensure_database_secret jobtrack-role-password-identity)"
role_password_pat_management="$(ensure_database_secret jobtrack-role-password-pat-management)"
role_password_pat_authentication="$(ensure_database_secret jobtrack-role-password-pat-authentication)"
# Not one of the six application connection strings -- no running service ever holds this one. It
# exists only so an operator can recover a locked or otherwise inaccessible account later via
# docker/emergency-reset.sh, run by the transient least-privilege helper job. See
# docs/operations/postgresql-cloud-run-deployment.md §"Recovering a locked or
# inaccessible account".
role_password_emergency_reset="$(ensure_database_secret jobtrack-role-password-emergency-reset)"
admin_password="$(ensure_enrolment_secret jobtrack-account-password-admin)"
user1_password="$(ensure_enrolment_secret jobtrack-account-password-user1)"
user2_password="$(ensure_enrolment_secret jobtrack-account-password-user2)"
data_protection_certificate_password=
ensure_data_protection_material

db_admin_password_version="$(secret_version jobtrack-db-admin-password)"
role_password_domain_version="$(secret_version jobtrack-role-password-domain)"
role_password_history_deletion_version="$(secret_version jobtrack-role-password-history-deletion)"
role_password_credential_administration_version="$(secret_version jobtrack-role-password-credential-administration)"
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
		--availability-type="$sql_availability_type" \
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
#
# --tier is reconciled here, not only at creation: an instance created by an earlier run predating the
# multi-instance work is still on the old shared-core tier, and the connection budget above is derived
# from *this* tier's memory bracket. Setting it only in the create branch would leave an existing
# instance at ~25 max_connections while the four pools are sized against 100 -- the pools would exhaust
# the server rather than their own limits, which is exactly the failure the budget exists to prevent.
# Changing tier restarts the instance, so expect brief downtime on the run that first applies it.
gcloud sql instances patch "$sql_instance" \
	--project="$project" \
	--tier="$sql_tier" \
	--availability-type="$sql_availability_type" \
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

deployed_sql_availability_type="$(gcloud sql instances describe "$sql_instance" \
	--project="$project" --format='value(settings.availabilityType)' | tr '[:upper:]' '[:lower:]')"
if [[ $deployed_sql_availability_type != "$sql_availability_type" ]]; then
	echo "ERROR: Cloud SQL availability is $deployed_sql_availability_type, expected $sql_availability_type." >&2
	exit 1
fi

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

# Maximum Pool Size is explicit on every connection string (ADR 0066 §11): the aggregate across the
# six distinct host pools must never exceed host_budget, validated above before any secret is
# written.
connection_string() {
	printf 'Host=%s;Database=%s;Username=%s;Password=%s;Maximum Pool Size=%s' "$db_host" "$sql_database" "$1" "$2" "$3"
}

echo "==> storing the six application connection strings"
put_secret jobtrack-cs-domain "$(connection_string jobtrack_domain_login "$role_password_domain" "$domain_pool_max_size")"
put_secret jobtrack-cs-history-deletion "$(connection_string jobtrack_history_deletion_login "$role_password_history_deletion" "$history_deletion_pool_max_size")"
put_secret jobtrack-cs-credential-administration "$(connection_string jobtrack_credential_administration_login "$role_password_credential_administration" "$credential_administration_pool_max_size")"
put_secret jobtrack-cs-identity "$(connection_string jobtrack_identity_login "$role_password_identity" "$identity_pool_max_size")"
put_secret jobtrack-cs-pat-management "$(connection_string jobtrack_pat_management_login "$role_password_pat_management" "$pat_management_pool_max_size")"
put_secret jobtrack-cs-pat-authentication "$(connection_string jobtrack_pat_authentication_login "$role_password_pat_authentication" "$pat_authentication_pool_max_size")"

cs_domain_version="$(secret_version jobtrack-cs-domain)"
cs_history_deletion_version="$(secret_version jobtrack-cs-history-deletion)"
cs_credential_administration_version="$(secret_version jobtrack-cs-credential-administration)"
cs_identity_version="$(secret_version jobtrack-cs-identity)"
cs_pat_management_version="$(secret_version jobtrack-cs-pat-management)"
cs_pat_authentication_version="$(secret_version jobtrack-cs-pat-authentication)"

# ---- IAM --------------------------------------------------------------------
# No GCS key-ring bucket: ADR 0066/plan Stage 2's data-protection key ring lives in PostgreSQL
# (data_protection_key, via DataProtection:Store=PostgreSql below) from this deployment's first
# release. There is no prior GCS-backed production key ring for this service to migrate -- Stage 2's
# Bridge A/Bridge B/Final sequence applies only when one already exists.

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

# The service gets the six application connection strings and nothing else. It cannot read the
# database admin password, so a compromise of the running app cannot escalate to the PostgreSQL
# superuser, and it cannot read the three account passwords either.
grant_secret_access "$run_service_account" \
	jobtrack-cs-domain jobtrack-cs-history-deletion jobtrack-cs-credential-administration jobtrack-cs-identity jobtrack-cs-pat-management jobtrack-cs-pat-authentication \
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
docker buildx build --builder="$buildx_builder" -f "$repo/Dockerfile.postgresql" --target provision \
	-t "$provision_image" --platform linux/amd64 --sbom=true --provenance=mode=max \
	--build-arg="SOURCE_REVISION_ID=$source_revision_id" \
	--label="org.opencontainers.image.revision=$source_revision" --push "$monorepo_root"

echo "==> building and pushing $serve_image (serve target: chiseled, web only)"
docker buildx build --builder="$buildx_builder" -f "$repo/Dockerfile.postgresql" \
	-t "$serve_image" --platform linux/amd64 --sbom=true --provenance=mode=max \
	--build-arg="SOURCE_REVISION_ID=$source_revision_id" \
	--label="org.opencontainers.image.revision=$source_revision" --push "$monorepo_root"

provision_digest="$(gcloud artifacts docker images describe --project="$project" "$provision_image" --format='value(image_summary.digest)')"
serve_digest="$(gcloud artifacts docker images describe --project="$project" "$serve_image" --format='value(image_summary.digest)')"
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
	done < <(docker buildx imagetools inspect --builder="$buildx_builder" "$image" --raw |
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
# max_instances is ADR 0066 §10's multi-instance policy, not a cost setting picked freely: the four
# formerly in-process stores (remembered page filters, both rate limiters, pending PAT delivery) are
# now PostgreSQL-backed or protected-cookie based (Stages 2-5), so a single-host ceiling is no longer
# a correctness requirement -- but Deployment__Topology=MultiInstance below still requires it, and
# DataProtection__Store/RateLimiting__Store still fail startup closed if unset.
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
candidate_smoke_paths=(/health/ready /Account/Login)
candidate_smoke_attempts=6
candidate_smoke_timeout_seconds=20

existing_service_host=
if gcloud run services describe "$service" --project="$project" --region="$region" >/dev/null 2>&1; then
	existing_url="$(gcloud run services describe "$service" \
		--project="$project" --region="$region" --format='value(status.url)')"
	existing_service_host="${existing_url#https://}"
fi

# `gcloud run deploy --set-secrets` promises replacement semantics, but file-mounted secrets receive
# generated volume names and gcloud 576.0.0 retained the previous generated volumes on every deploy.
# Clear both collections explicitly before recreating the two required mounts. This also permanently
# reconciles the retired GCS key-ring volume without carrying a special-case volume name forever.

# A TCP startup probe, and deliberately no liveness probe. Cloud Run addresses a probe to the container
# directly, with a Host header that is not this deployment's public name, and AllowedHosts -- which
# Program.cs requires to be a real host list outside Development -- rejects it with 400 before routing
# ever sees the path. An HTTP probe therefore fails every attempt and the revision never becomes ready.
# gcloud's probe flags carry no way to set a Host header, so httpGet cannot be reconciled with host
# filtering here.
#
# That leaves the two probes in different positions, because gcloud accepts different keys for each:
#   --startup-probe  accepts tcpSocket.port, so it can sidestep the Host header entirely. TCP is also
#                    what Cloud Run's own default startup probe uses.
#   --liveness-probe accepts only httpGet.* and grpc.*, so it has no form this deployment can satisfy.
#                    Passed as "" -- gcloud's documented way to remove a probe -- rather than simply
#                    left out. Omitting the flag does not remove an inherited probe: `gcloud run
#                    deploy` merges, exactly as it does for volumes above, so an httpGet liveness probe
#                    configured by an earlier revision survived into later ones, answered 400 on every
#                    check, and had Cloud Run shutting an instance down roughly every 90 seconds.
#
# The gap that leaves is worth stating rather than glossing: startup is proven only as far as "the
# listener accepts connections", and nothing subsequently restarts a process that wedges while still
# holding its socket. /health/live and /health/ready remain the honest signals for an operator or an
# external checker able to send a real Host header. Closing this properly means giving host filtering
# a way to admit the probe, which is a change to the application, not to this script.
#
# Note that only a *tagged* revision exercises a probe at deploy time: an untagged --no-traffic
# revision needs no running instance, so its probe never runs and it reports success regardless.
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
		--min="$min_instances" \
		--max="$max_instances" \
		--min-instances=default \
		--max-instances=default \
		--concurrency="$container_concurrency" \
		--no-session-affinity \
		--set-cloudsql-instances="$instance_connection_name" \
		--clear-volume-mounts \
		--clear-volumes \
		--startup-probe="tcpSocket.port=8080,periodSeconds=10,failureThreshold=24,timeoutSeconds=5" \
		--liveness-probe="" \
		--set-env-vars="^@^ForwardedHeaders__KnownNetworks__0=0.0.0.0/0@AllowedHosts=$allowed_hosts@Deployment__Topology=MultiInstance@DataProtection__Store=PostgreSql@RateLimiting__Store=PostgreSql@Security__RequireSecureCookies=true" \
		--set-secrets="ConnectionStrings__JobTrackDomain=jobtrack-cs-domain:$cs_domain_version,ConnectionStrings__JobTrackHistoryDeletion=jobtrack-cs-history-deletion:$cs_history_deletion_version,ConnectionStrings__JobTrackCredentialAdministration=jobtrack-cs-credential-administration:$cs_credential_administration_version,ConnectionStrings__JobTrackIdentity=jobtrack-cs-identity:$cs_identity_version,ConnectionStrings__JobTrackPatManagement=jobtrack-cs-pat-management:$cs_pat_management_version,ConnectionStrings__JobTrackPatAuthentication=jobtrack-cs-pat-authentication:$cs_pat_authentication_version,$certificate_mount_path=jobtrack-data-protection-certificate:$data_protection_certificate_version,$certificate_password_mount_path=jobtrack-data-protection-certificate-password:$data_protection_certificate_password_version" \
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
	for secret in "${provision_secrets[@]}"; do
		# Retired enrolment secrets deliberately have no enabled version and are not granted back to
		# the job merely because their empty Secret Manager resource remains as a lifecycle marker.
		if ! secret_exists "$secret" || [[ -z $(secret_version "$secret") ]]; then
			continue
		fi
		gcloud secrets add-iam-policy-binding "$secret" \
			--project="$project" \
			--member="serviceAccount:$provision_service_account" \
			--role=roles/secretmanager.secretAccessor \
			--condition="$provision_access_condition" --quiet >/dev/null
	done
}

provision_secret_bindings="JOBTRACK_DB_ADMIN_PASSWORD=jobtrack-db-admin-password:$db_admin_password_version,JOBTRACK_ROLE_PASSWORD_DOMAIN=jobtrack-role-password-domain:$role_password_domain_version,JOBTRACK_ROLE_PASSWORD_HISTORY_DELETION=jobtrack-role-password-history-deletion:$role_password_history_deletion_version,JOBTRACK_ROLE_PASSWORD_CREDENTIAL_ADMINISTRATION=jobtrack-role-password-credential-administration:$role_password_credential_administration_version,JOBTRACK_ROLE_PASSWORD_IDENTITY=jobtrack-role-password-identity:$role_password_identity_version,JOBTRACK_ROLE_PASSWORD_PAT_MANAGEMENT=jobtrack-role-password-pat-management:$role_password_pat_management_version,JOBTRACK_ROLE_PASSWORD_PAT_AUTHENTICATION=jobtrack-role-password-pat-authentication:$role_password_pat_authentication_version,JOBTRACK_ROLE_PASSWORD_EMERGENCY_RESET=jobtrack-role-password-emergency-reset:$role_password_emergency_reset_version"
if [[ -n $admin_password_version ]]; then
	provision_secret_bindings="$provision_secret_bindings,JOBTRACK_ADMIN_PASSWORD=jobtrack-account-password-admin:$admin_password_version"
fi
if [[ -n $user1_password_version ]]; then
	provision_secret_bindings="$provision_secret_bindings,JOBTRACK_USER1_PASSWORD=jobtrack-account-password-user1:$user1_password_version"
fi
if [[ -n $user2_password_version ]]; then
	provision_secret_bindings="$provision_secret_bindings,JOBTRACK_USER2_PASSWORD=jobtrack-account-password-user2:$user2_password_version"
fi

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
	--set-env-vars="^@^JOBTRACK_DB_HOST=$db_host@JOBTRACK_DB_NAME=$sql_database@JOBTRACK_DB_ADMIN_USER=postgres@JOBTRACK_ADMIN_USERNAME=$admin_username@JOBTRACK_ADMIN_DISPLAY_NAME=$admin_display_name@JOBTRACK_USER1_USERNAME=$user1_username@JOBTRACK_USER1_DISPLAY_NAME=$user1_display_name@JOBTRACK_USER1_ROLES=$user1_roles@JOBTRACK_USER2_USERNAME=$user2_username@JOBTRACK_USER2_DISPLAY_NAME=$user2_display_name@JOBTRACK_USER2_ROLES=$user2_roles@JOBTRACK_TIME_ZONE=$time_zone@JOBTRACK_EXPECTED_MAX_CONNECTIONS=$database_max_connections" \
	--set-secrets="$provision_secret_bindings" \
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
	local path
	for path in "${candidate_smoke_paths[@]}"; do
		echo "==> smoke-testing $candidate_url$path"
		curl --fail --silent --show-error \
			--retry "$candidate_smoke_attempts" \
			--retry-all-errors \
			--max-time "$candidate_smoke_timeout_seconds" \
			"$candidate_url$path" >/dev/null
	done
}

# ADR 0066/plan Stage 8 item 5: correctness never relies on session affinity, so a service with it
# enabled -- by drift, a manual `gcloud run services update`, or a future edit to this script --
# fails the deploy rather than silently reintroducing a sticky-routing assumption. Searched
# recursively rather than by a fixed JSON path, since Cloud Run's exposed field location has moved
# across API revisions.
deployed_service_json="$(gcloud run services describe "$service" --project="$project" --region="$region" --format=json)"
deployed_min_instances="$(jq -r '.metadata.annotations["run.googleapis.com/minScale"] // "0"' <<<"$deployed_service_json")"
deployed_max_instances="$(jq -r '.metadata.annotations["run.googleapis.com/maxScale"] // empty' <<<"$deployed_service_json")"
deployed_container_concurrency="$(jq -r '.spec.template.spec.containerConcurrency // empty' <<<"$deployed_service_json")"
deployed_revision_min_instances="$(jq -r '.spec.template.metadata.annotations["autoscaling.knative.dev/minScale"] // empty' <<<"$deployed_service_json")"
deployed_revision_max_instances="$(jq -r '.spec.template.metadata.annotations["autoscaling.knative.dev/maxScale"] // empty' <<<"$deployed_service_json")"
session_affinity_enabled="$(jq '[.. | .sessionAffinity? // empty] | any' <<<"$deployed_service_json")"
deployed_secure_cookies="$(jq -r '[.spec.template.spec.containers[0].env[]
	| select(.name == "Security__RequireSecureCookies")][0].value // empty' <<<"$deployed_service_json")"
deployed_volume_count="$(jq '(.spec.template.spec.volumes // []) | length' <<<"$deployed_service_json")"
deployed_volume_mount_count="$(jq '(.spec.template.spec.containers[0].volumeMounts // []) | length' <<<"$deployed_service_json")"
unmounted_volume_count="$(jq '
	[.spec.template.spec.volumes[]?.name] as $volumes
	| [.spec.template.spec.containers[0].volumeMounts[]?.name] as $mounts
	| [$volumes[] | select(. as $volume | ($mounts | index($volume)) == null)]
	| length' <<<"$deployed_service_json")"
if [[ $deployed_min_instances != "$min_instances" || $deployed_max_instances != "$max_instances" ]]; then
	echo "ERROR: Cloud Run service-level scaling drifted from min=$min_instances/max=$max_instances." >&2
	exit 1
fi
if [[ $deployed_container_concurrency != "$container_concurrency" ]]; then
	echo "ERROR: Cloud Run container concurrency is $deployed_container_concurrency, expected $container_concurrency." >&2
	exit 1
fi
# --max-instances=default is documented to remove the revision-level autoscaling.knative.dev/maxScale
# annotation, but on gcloud 576.0.0 it instead resolves to a literal platform default (20, confirmed
# empirically against this project) rather than an absent key -- there is no flag on this SDK that
# actually clears it. A revision-level maxScale looser than the service-level cap is harmless,
# though: Cloud Run enforces the tighter of the two, so max_instances above remains the real ceiling
# regardless of a larger, non-binding revision-level value. Only a revision-level max STRICTER than
# the service-level cap -- or any revision-level min, which has no such safe default -- would defeat
# the connection budget, so those are what's actually checked.
if [[ -n $deployed_revision_min_instances ]]; then
	echo "ERROR: revision-level minimum-instance scaling remains configured; the database budget requires one service-level cap." >&2
	exit 1
fi
if [[ -n $deployed_revision_max_instances ]] && ((deployed_revision_max_instances < max_instances)); then
	echo "ERROR: revision-level max instances ($deployed_revision_max_instances) is stricter than the service-level cap ($max_instances)." >&2
	exit 1
fi
if [[ $session_affinity_enabled == true ]]; then
	echo "ERROR: Cloud Run session affinity is enabled on $service; multi-instance correctness must not depend on it." >&2
	exit 1
fi
if [[ $deployed_secure_cookies != true ]]; then
	echo "ERROR: the PostgreSQL service must force Secure antiforgery and TempData cookies." >&2
	exit 1
fi
if [[ $deployed_volume_count != 2 || $deployed_volume_mount_count != 2 || $unmounted_volume_count != 0 ]]; then
	echo "ERROR: Cloud Run must have exactly two mounted secret volumes; found $deployed_volume_count volumes, $deployed_volume_mount_count mounts, and $unmounted_volume_count unmounted volumes." >&2
	exit 1
fi

smoke_test_candidate

echo "==> promoting the validated candidate to 100% traffic"
gcloud run services update-traffic "$service" \
	--project="$project" --region="$region" \
	--to-tags="$candidate_tag=100" --quiet >/dev/null
gcloud run services update-traffic "$service" \
	--project="$project" --region="$region" \
	--remove-tags="$candidate_tag" --quiet >/dev/null

# Newest-first, so tail keeps everything past revision_keep_count. The serving revision is always
# among the newest and gcloud refuses to delete a revision carrying live traffic, so this never
# targets it. Best-effort: a revision gcloud won't delete for some other reason should not fail the
# deploy that already succeeded.
echo "==> pruning old revisions, keeping the $revision_keep_count most recent"
while read -r stale_revision; do
	[[ -n $stale_revision ]] || continue
	gcloud run revisions delete "$stale_revision" \
		--project="$project" --region="$region" --quiet >/dev/null 2>&1 || true
done < <(gcloud run revisions list \
	--service="$service" --project="$project" --region="$region" \
	--sort-by='~metadata.creationTimestamp' --format='value(metadata.name)' |
	tail -n "+$((revision_keep_count + 1))")

url="https://$alternate_service_host"

echo
echo "==> deployed: $url"
echo "==> database: Cloud SQL $instance_connection_name (persistent -- survives every recycle and redeploy)"
echo "==> no example job nodes were installed; the tree below the root node is empty"
echo
if [[ -n $admin_password_version || -n $user1_password_version || -n $user2_password_version ]]; then
	echo "    An enabled enrolment secret remains for the accounts below. This means the secret has"
	echo "    not been retired -- it does NOT mean the value below is still that account's password."
	echo "    Sign-in with the SECRET forces a change (ADR 0023) only the FIRST time it is used; an"
	echo "    account already in active use may have changed its password (and enabled 2FA) long ago,"
	echo "    in which case this value is stale and irrelevant. Confirm with each user before treating"
	echo "    the secret as live, and retrieve a credential only when handing it to its intended user:"
	echo
	printf '      %-10s %-36s %s\n' "USERNAME" "SECRET" "ROLES"
	if [[ -n $admin_password_version ]]; then
		printf '      %-10s %-36s %s\n' "$admin_username" "jobtrack-account-password-admin" "Administrator"
	fi
	if [[ -n $user1_password_version ]]; then
		printf '      %-10s %-36s %s\n' "$user1_username" "jobtrack-account-password-user1" "$user1_roles"
	fi
	if [[ -n $user2_password_version ]]; then
		printf '      %-10s %-36s %s\n' "$user2_username" "jobtrack-account-password-user2" "$user2_roles"
	fi
	echo
	echo "    After all listed users change them: ./scripts/retire-cloudrun-enrolment-secrets.sh $project --confirm-passwords-changed"
	echo
else
	echo "    One-time enrolment credentials are retired; this deployment did not recreate them."
	echo
fi
echo "Tear down when done -- the Cloud SQL instance bills continuously and does not scale to zero:"
echo "  gcloud run services delete $service --project=$project --region=$region --quiet"
echo "  gcloud run jobs delete $provision_job --project=$project --region=$region --quiet"
echo "  gcloud sql instances delete $sql_instance --project=$project --quiet"

# Reached only when every step above succeeded; see the cleanup trap.
deployment_completed=true
