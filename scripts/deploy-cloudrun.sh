#!/usr/bin/env bash
#
# Build the SQLite demo image (see ../Dockerfile) with a fresh, random admin
# password and deploy it to Cloud Run as a throwaway reachability smoke test.
# See docs/operations/docker-image.md, "Cloud Run smoke test", for why this
# exists and what it deliberately does not give you (no persistent volume,
# no persistent state).
#
# The image bakes in three accounts (see ../Dockerfile): a privileged ADMIN, a
# normal DEMO user (demo/demo-jobtrack-1234) that owns the sample job trees, and a
# REQUESTER (requester/requester-jobtrack-1234) with six requests. Both non-admin
# credentials are deliberately published and reusable; the admin credential must
# not be, since Cloud Run is network-exposed. This script therefore always
# generates a fresh, random ADMIN_PASSWORD, passes it as a build arg, and prints
# it once at the end since nothing else records it (not committed, not logged by
# gcloud, regenerated on every run). The two demo passwords stay published.
#
# Usage: ./scripts/deploy-cloudrun.sh <gcp-project-id> [region]
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
monorepo_root="$(cd "$repo/.." && pwd)"

project="${1:?Usage: $0 <gcp-project-id> [region]}"
# The persistent PostgreSQL deployment's project. This demo is publicly reachable with published
# credentials, so it must never share a project (and therefore an IAM policy) with jobtrack-web-pg's
# Cloud SQL instance, secrets, or data-protection key ring -- deploy it to jobtrack-demo-projects
# instead. See docs/plans/2026-08-06-cloudrun-persistent-isolation-plan.md #2.1/#2.3.
persistent_project="project-e2ce9938-0f7b-48a8-b0d"
if [[ "$project" == "$persistent_project" ]]; then
  echo "ERROR: $persistent_project hosts the persistent jobtrack-web-pg deployment." >&2
  echo "Deploying this demo there would re-open the co-tenancy risk closed by" >&2
  echo "docs/plans/2026-08-06-cloudrun-persistent-isolation-plan.md. Use jobtrack-demo-projects." >&2
  exit 1
fi
# europe-west1 (Belgium) is a Tier 1 GCP pricing region; europe-west2 (London) is Tier 2,
# so the Always Free allowance and per-unit cost are both worse there for no functional gain.
region="${2:-europe-west1}"
service="jobtrack-web"
repository="cloud-run-source-deploy"
# Cloud Run never deletes a revision on its own, so every deploy leaves one behind. Idle revisions
# cost nothing under scale-to-zero, but keep only a few for rollback rather than letting them
# accumulate indefinitely.
revision_keep_count=3
image="$region-docker.pkg.dev/$project/$repository/$service:latest"
orbstack_socket="${HOME}/.orbstack/run/docker.sock"
# Deliberately no roles: this demo reads no secret, bucket, or database, so it
# needs nothing beyond what its own image carries. Kept off the default compute
# service account, which typically holds project-wide roles (e.g.
# cloudbuild.builds.builder) that would let a compromise of this public,
# credential-published demo reach the persistent deployment's resources -- see
# docs/plans/2026-08-06-cloudrun-persistent-isolation-plan.md #2.1.
demo_service_account="demo-run@$project.iam.gserviceaccount.com"

# Keep this distinct from deploy-cloudrun-postgresql.sh's admin_username (adminpg): the two images are
# separate databases with no technical conflict, but sharing the same username across both makes it
# easy to confuse or overwrite one deployment's admin credential with the other's when juggling both.
admin_username="admin"
admin_password="$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-20)"
demo_username="demo"
demo_password="demo-jobtrack-1234"
requester_username="requester"
requester_password="requester-jobtrack-1234"
# The build stage's own `git describe` fallback always fails inside the container (no .git in the
# build context), so the login page's build-revision chip needs the real value passed in from here.
source_revision_id="$(git -C "$repo" describe --tags --always --dirty --abbrev=12)"

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

echo "==> building $image (ADMIN_PASSWORD freshly generated, demo credentials published, platform linux/amd64)"
docker build -f "$repo/Dockerfile" \
  -t "$image" \
  --build-arg ADMIN_PASSWORD="$admin_password" \
  --build-arg DEMO_PASSWORD="$demo_password" \
  --build-arg SOURCE_REVISION_ID="$source_revision_id" \
  --platform linux/amd64 \
  "$monorepo_root"

echo "==> pushing to Artifact Registry"
gcloud auth configure-docker "$region-docker.pkg.dev" --project="$project" --quiet
docker push "$image"

if ! gcloud iam service-accounts describe "$demo_service_account" --project="$project" >/dev/null 2>&1; then
  echo "==> creating $demo_service_account (no roles, deliberately)"
  gcloud iam service-accounts create demo-run \
    --project="$project" \
    --display-name="Disposable demo services (no roles, deliberately)"
fi

echo "==> deploying to Cloud Run ($service, $region)"
# Kestrel__Endpoints__Http__Url: Cloud Run's fully-managed product terminates
# TLS at its own front end and always proxies to the container over plain
# HTTP on $PORT -- it never reaches the image's baked-in HTTPS/self-signed
# cert on :8443. ForwardedHeaders__KnownNetworks__0=0.0.0.0/0: Program.cs
# requires a trusted-proxy entry outside Development to accept
# X-Forwarded-Proto; trusting any source here is reasonable specifically
# because Cloud Run does not allow direct public access to the container --
# only Google's own front end can ever set that header. AllowedHosts likewise
# has to be set (Program.cs rejects an unset or '*' value): the service's own
# URL is not known until after this deploy returns, so it is scoped to the
# *.run.app suffix Cloud Run allocates from rather than the exact hostname --
# narrower than the catch-all, wider than a single host.
gcloud run deploy "$service" \
  --project="$project" \
  --region="$region" \
  --image="$image" \
  --port=8080 \
  --allow-unauthenticated \
  --min-instances=0 \
  --max-instances=1 \
  --service-account="$demo_service_account" \
  --set-env-vars="Kestrel__Endpoints__Http__Url=http://+:8080,ForwardedHeaders__KnownNetworks__0=0.0.0.0/0,AllowedHosts=*.run.app" \
  --quiet

url="$(gcloud run services describe "$service" --project="$project" --region="$region" --format='value(status.url)')"

# Newest-first, so tail keeps everything past revision_keep_count. gcloud refuses to delete a
# revision carrying live traffic, so this never targets the one just deployed.
echo "==> pruning old revisions, keeping the $revision_keep_count most recent"
while read -r stale_revision; do
  [[ -n $stale_revision ]] || continue
  gcloud run revisions delete "$stale_revision" \
    --project="$project" --region="$region" --quiet >/dev/null 2>&1 || true
done < <(gcloud run revisions list \
  --service="$service" --project="$project" --region="$region" \
  --sort-by='~metadata.creationTimestamp' --format='value(metadata.name)' |
  tail -n "+$((revision_keep_count + 1))")

echo
echo "==> deployed: $url"
echo "==> sign in with any baked-in account:"
echo
echo "      DEMO (normal user, owns the sample job trees) -- published, share freely:"
echo "        username: $demo_username"
echo "        password: $demo_password"
echo
echo "      REQUESTER (six open/closed requests) -- published, share freely:"
echo "        username: $requester_username"
echo "        password: $requester_password"
echo
echo "      ADMIN (privileged: account/role management) -- random, recorded ONLY here:"
echo "        username: $admin_username"
echo "        password: $admin_password"
echo
echo "This service has no persistent volume. Every recycle (scale-to-zero cold start, redeploy, or"
echo "a maintenance/load recycle) wipes the database back to this baked seed -- nothing you change"
echo "in the app persists. Change either password in the UI and it reverts to the one above on the"
echo "next recycle; the only durable password is one baked in via --build-arg at build time. The"
echo "random admin password also stops working once a fresh revision is deployed. Tear it down when done:"
echo "  gcloud run services delete $service --project=$project --region=$region --quiet"
