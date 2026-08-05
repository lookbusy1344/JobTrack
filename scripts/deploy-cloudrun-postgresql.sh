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
# others -- each with a randomly generated password, printed once at the end. All three force a
# password change on first sign-in (the ADR 0023 default), so those printed values are one-time
# enrolment credentials, not standing passwords.
#
# Idempotent and re-runnable. Every resource is created only if absent, and every secret keeps its
# existing value rather than being regenerated, so a second run prints the same three passwords,
# applies any new schema versions, and redeploys the current build without locking you out.
#
# Usage: ./scripts/deploy-cloudrun-postgresql.sh <gcp-project-id> [region]
set -euo pipefail

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
serve_image="$region-docker.pkg.dev/$project/$repository/$service:latest"
provision_image="$region-docker.pkg.dev/$project/$repository/$provision_job:latest"
key_bucket="$project-jobtrack-dpkeys"
key_volume="dpkeys"
key_mount_path="/var/lib/jobtrack/keys"
orbstack_socket="${HOME}/.orbstack/run/docker.sock"

# The three accounts. Roles are EmployeeRole names; the first is the account's initial role and any
# remainder are granted afterwards (ADR 0023).
admin_username="admin"
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
	--project="$project" --quiet

# Two dedicated service accounts, NOT the default compute service account. That default is created
# with the project Editor role in most projects, so running a public web app as it would mean an
# application compromise carries write access to every resource in the project. These start with no
# roles at all and are granted exactly what each needs, nothing more.
#
# They are also separated from each other: the service can read the four application connection
# strings and write the key ring, and cannot read the database admin password or any account
# password. The provisioning job is the mirror image. Neither can read the other's secrets.
run_service_account="jobtrack-run@$project.iam.gserviceaccount.com"
provision_service_account="jobtrack-provision-sa@$project.iam.gserviceaccount.com"

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

echo "==> ensuring secrets exist (existing values are preserved, never regenerated)"
db_admin_password="$(ensure_generated_secret jobtrack-db-admin-password)"
role_password_domain="$(ensure_generated_secret jobtrack-role-password-domain)"
role_password_identity="$(ensure_generated_secret jobtrack-role-password-identity)"
role_password_pat_management="$(ensure_generated_secret jobtrack-role-password-pat-management)"
role_password_pat_authentication="$(ensure_generated_secret jobtrack-role-password-pat-authentication)"
admin_password="$(ensure_generated_secret jobtrack-account-password-admin)"
user1_password="$(ensure_generated_secret jobtrack-account-password-user1)"
user2_password="$(ensure_generated_secret jobtrack-account-password-user2)"

# ---- Cloud SQL --------------------------------------------------------------

if gcloud sql instances describe "$sql_instance" --project="$project" >/dev/null 2>&1; then
	echo "==> Cloud SQL instance $sql_instance already exists"
else
	echo "==> creating Cloud SQL instance $sql_instance ($sql_version, $sql_tier) -- this takes several minutes"
	gcloud sql instances create "$sql_instance" \
		--project="$project" \
		--region="$region" \
		--database-version="$sql_version" \
		--edition=ENTERPRISE \
		--tier="$sql_tier" \
		--storage-auto-increase \
		--backup-start-time="$sql_backup_start_time" \
		--enable-point-in-time-recovery \
		--ssl-mode=ENCRYPTED_ONLY \
		--root-password="$db_admin_password" \
		--quiet
fi

# Applied unconditionally so an instance created by an earlier run is brought up to the same posture.
# No authorized networks are ever added (the create above adds none, and this does not either), so
# the public IP accepts no direct client at all -- every connection arrives through the Cloud SQL
# connector, which authenticates with IAM. --ssl-mode=ENCRYPTED_ONLY is defence in depth behind that:
# if an authorized network were ever added by hand, an unencrypted connection still would not work.
gcloud sql instances patch "$sql_instance" \
	--project="$project" --ssl-mode=ENCRYPTED_ONLY --quiet >/dev/null

# Unconditional, so the instance's password always matches the secret even if the two were created in
# separate runs (or the secret was recreated after a partial teardown).
gcloud sql users set-password postgres \
	--instance="$sql_instance" --project="$project" --password="$db_admin_password" --quiet

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
	gcloud storage buckets update "gs://$key_bucket" --versioning --quiet >/dev/null
fi

# ---- IAM --------------------------------------------------------------------

echo "==> granting each service account only what it needs"

# Both need to open the Cloud SQL socket. roles/cloudsql.client grants connect-and-authenticate only:
# it is not a database privilege, so what each identity can actually do inside the database is still
# decided by the PostgreSQL role its connection string authenticates as.
for account in "$run_service_account" "$provision_service_account"; do
	gcloud projects add-iam-policy-binding "$project" \
		--member="serviceAccount:$account" \
		--role=roles/cloudsql.client --condition=None --quiet >/dev/null
done

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
	jobtrack-cs-domain jobtrack-cs-identity jobtrack-cs-pat-management jobtrack-cs-pat-authentication

# The provisioning job gets the credentials it must create things with, and no connection string.
grant_secret_access "$provision_service_account" \
	jobtrack-db-admin-password \
	jobtrack-role-password-domain jobtrack-role-password-identity \
	jobtrack-role-password-pat-management jobtrack-role-password-pat-authentication \
	jobtrack-account-password-admin jobtrack-account-password-user1 jobtrack-account-password-user2

# Only the service writes the key ring; the provisioning job has no business reading it.
gcloud storage buckets add-iam-policy-binding "gs://$key_bucket" \
	--member="serviceAccount:$run_service_account" \
	--role=roles/storage.objectAdmin --quiet >/dev/null

# ---- build and push ---------------------------------------------------------
# --platform linux/amd64 matters on Apple Silicon: Cloud Run runs amd64 and the local daemon defaults
# to the host's arm64. The build context is the monorepo root, not JobTrack/ -- see the Dockerfile's
# header comment.

if gcloud artifacts repositories describe "$repository" --location="$region" --project="$project" >/dev/null 2>&1; then
	echo "==> Artifact Registry repository $repository already exists"
else
	echo "==> creating Artifact Registry repository $repository"
	gcloud artifacts repositories create "$repository" \
		--repository-format=docker --location="$region" --project="$project" --quiet
fi

echo "==> building $provision_image (provisioning target: shell, psql, AdminCli)"
docker build -f "$repo/Dockerfile.postgresql" --target provision \
	-t "$provision_image" --platform linux/amd64 "$monorepo_root"

echo "==> building $serve_image (serve target: chiseled, web only)"
docker build -f "$repo/Dockerfile.postgresql" \
	-t "$serve_image" --platform linux/amd64 "$monorepo_root"

echo "==> pushing to Artifact Registry"
gcloud auth configure-docker "$region-docker.pkg.dev" --project="$project" --quiet
docker push "$provision_image"
docker push "$serve_image"

# ---- provisioning job -------------------------------------------------------
# Schema, four login roles, and the three accounts. Every step is skipped if already done, so this
# also applies new schema versions to an existing database. --max-retries=0: a failure here should
# surface, not be retried against a half-provisioned database.

echo "==> deploying and running the provisioning job"
gcloud run jobs deploy "$provision_job" \
	--project="$project" \
	--region="$region" \
	--image="$provision_image" \
	--service-account="$provision_service_account" \
	--set-cloudsql-instances="$instance_connection_name" \
	--max-retries=0 \
	--task-timeout=15m \
	--set-env-vars="^@^JOBTRACK_DB_HOST=$db_host@JOBTRACK_DB_NAME=$sql_database@JOBTRACK_DB_ADMIN_USER=postgres@JOBTRACK_ADMIN_USERNAME=$admin_username@JOBTRACK_ADMIN_DISPLAY_NAME=$admin_display_name@JOBTRACK_USER1_USERNAME=$user1_username@JOBTRACK_USER1_DISPLAY_NAME=$user1_display_name@JOBTRACK_USER1_ROLES=$user1_roles@JOBTRACK_USER2_USERNAME=$user2_username@JOBTRACK_USER2_DISPLAY_NAME=$user2_display_name@JOBTRACK_USER2_ROLES=$user2_roles@JOBTRACK_TIME_ZONE=$time_zone" \
	--set-secrets="JOBTRACK_DB_ADMIN_PASSWORD=jobtrack-db-admin-password:latest,JOBTRACK_ROLE_PASSWORD_DOMAIN=jobtrack-role-password-domain:latest,JOBTRACK_ROLE_PASSWORD_IDENTITY=jobtrack-role-password-identity:latest,JOBTRACK_ROLE_PASSWORD_PAT_MANAGEMENT=jobtrack-role-password-pat-management:latest,JOBTRACK_ROLE_PASSWORD_PAT_AUTHENTICATION=jobtrack-role-password-pat-authentication:latest,JOBTRACK_ADMIN_PASSWORD=jobtrack-account-password-admin:latest,JOBTRACK_USER1_PASSWORD=jobtrack-account-password-user1:latest,JOBTRACK_USER2_PASSWORD=jobtrack-account-password-user2:latest" \
	--execute-now --wait --quiet

# ---- service ----------------------------------------------------------------
# ForwardedHeaders__KnownNetworks__0=0.0.0.0/0: Program.cs requires a trusted-proxy entry outside
# Development to accept X-Forwarded-Proto, and trusting any source is reasonable specifically because
# Cloud Run does not allow direct public access to the container -- only Google's own front end can
# ever set that header. AllowedHosts likewise has to be set (Program.cs rejects an unset or '*'
# value); the service's own URL is not known until this deploy returns, so it is scoped to the
# *.run.app suffix Cloud Run allocates from.
#
# --max-instances=1 IS NOT A COST SETTING. Four in-process stores (remembered page filters, the login
# rate limiter, pending PAT delivery, the external API rate limiter) are not shared between
# instances, and under two they fail silently rather than refusing to start -- see
# docs/operations/production-deployment.md §"In-process state that breaks under a second web
# instance". Raising this is a code change first.

echo "==> deploying the Cloud Run service"
gcloud run deploy "$service" \
	--project="$project" \
	--region="$region" \
	--image="$serve_image" \
	--service-account="$run_service_account" \
	--port=8080 \
	--allow-unauthenticated \
	--min-instances=0 \
	--max-instances=1 \
	--set-cloudsql-instances="$instance_connection_name" \
	--add-volume="name=$key_volume,type=cloud-storage,bucket=$key_bucket" \
	--add-volume-mount="volume=$key_volume,mount-path=$key_mount_path" \
	--set-env-vars="ForwardedHeaders__KnownNetworks__0=0.0.0.0/0,AllowedHosts=*.run.app" \
	--set-secrets="ConnectionStrings__JobTrackDomain=jobtrack-cs-domain:latest,ConnectionStrings__JobTrackIdentity=jobtrack-cs-identity:latest,ConnectionStrings__JobTrackPatManagement=jobtrack-cs-pat-management:latest,ConnectionStrings__JobTrackPatAuthentication=jobtrack-cs-pat-authentication:latest" \
	--quiet

url="$(gcloud run services describe "$service" --project="$project" --region="$region" --format='value(status.url)')"

# Second pass, now that the hostname exists: narrow AllowedHosts from the *.run.app suffix to this
# service's own hosts. The suffix was only ever a bootstrap value -- it accepts a Host header naming
# any Cloud Run service in the world, which is the kind of latitude host-header checks exist to
# remove. This costs one extra revision on a first deploy and is a no-op on every later run.
#
# BOTH hostnames must be listed. Cloud Run serves a service on two names -- the legacy
# <service>-<hash>-<regioncode>.a.run.app that status.url reports, and the newer
# <service>-<project-number>.<region>.run.app that `gcloud run deploy` prints -- and neither is an
# alias or redirect of the other. Allowing only status.url's makes the printed URL return 400 from
# the host filter, which reads exactly like a broken deployment. AllowedHosts is ';'-separated.
project_number="$(gcloud projects describe "$project" --format='value(projectNumber)')"
service_host="${url#https://}"
alternate_service_host="$service-$project_number.$region.run.app"
allowed_hosts="$service_host;$alternate_service_host"
current_allowed_hosts="$(gcloud run services describe "$service" --project="$project" --region="$region" \
	--format='value(spec.template.spec.containers[0].env.filter("name:AllowedHosts").extract("value").flatten())')"
if [[ "$current_allowed_hosts" != "$allowed_hosts" ]]; then
	echo "==> narrowing AllowedHosts to $allowed_hosts"
	# '^@^' picks a delimiter other than ',' so the ';'-separated value survives gcloud's own parsing.
	gcloud run services update "$service" \
		--project="$project" --region="$region" \
		--update-env-vars="^@^AllowedHosts=$allowed_hosts" --quiet >/dev/null
fi

echo
echo "==> deployed: $url"
echo "==> database: Cloud SQL $instance_connection_name (persistent -- survives every recycle and redeploy)"
echo "==> no example job nodes were installed; the tree below the root node is empty"
echo
echo "    Three accounts, all with randomly generated passwords. EACH MUST BE CHANGED ON FIRST"
echo "    SIGN-IN (ADR 0023), so these are one-time enrolment credentials:"
echo
printf '      %-10s %-22s %s\n' "USERNAME" "PASSWORD" "ROLES"
printf '      %-10s %-22s %s\n' "$admin_username" "$admin_password" "Administrator"
printf '      %-10s %-22s %s\n' "$user1_username" "$user1_password" "$user1_roles"
printf '      %-10s %-22s %s\n' "$user2_username" "$user2_password" "$user2_roles"
echo
echo "    They are also in Secret Manager (jobtrack-account-password-{admin,user1,user2}), so a"
echo "    re-run of this script prints the same values rather than new ones."
echo
echo "Tear down when done -- the Cloud SQL instance bills continuously and does not scale to zero:"
echo "  gcloud run services delete $service --project=$project --region=$region --quiet"
echo "  gcloud run jobs delete $provision_job --project=$project --region=$region --quiet"
echo "  gcloud sql instances delete $sql_instance --project=$project --quiet"
echo "  gcloud storage rm -r gs://$key_bucket"
