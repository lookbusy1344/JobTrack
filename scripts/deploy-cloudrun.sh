#!/usr/bin/env bash
#
# Build the SQLite demo image (see ../Dockerfile) with a fresh, random demo
# password and deploy it to Cloud Run as a throwaway reachability smoke test.
# See docs/operations/docker-image.md, "Cloud Run smoke test", for why this
# exists and what it deliberately does not give you (no persistent volume,
# no fixed credential).
#
# The image bakes in two accounts (see ../Dockerfile): a privileged ADMIN and a
# normal DEMO user (demo/demo1234) that owns the sample job trees. The demo
# credential is deliberately published and reusable; the admin credential must
# not be, since Cloud Run is network-exposed. This script therefore always
# generates a fresh, random ADMIN_PASSWORD, passes it as a build arg, and prints
# it once at the end since nothing else records it (not committed, not logged by
# gcloud, regenerated on every run). The demo password stays demo1234.
#
# Usage: ./scripts/deploy-cloudrun.sh <gcp-project-id> [region]
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
monorepo_root="$(cd "$repo/.." && pwd)"

project="${1:?Usage: $0 <gcp-project-id> [region]}"
# europe-west1 (Belgium) is a Tier 1 GCP pricing region; europe-west2 (London) is Tier 2,
# so the Always Free allowance and per-unit cost are both worse there for no functional gain.
region="${2:-europe-west1}"
service="jobtrack-web"
repository="cloud-run-source-deploy"
image="$region-docker.pkg.dev/$project/$repository/$service:latest"
orbstack_socket="${HOME}/.orbstack/run/docker.sock"

admin_username="admin"
admin_password="$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-20)"
demo_username="demo"
demo_password="demo1234"

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

echo "==> building $image (ADMIN_PASSWORD freshly generated, demo password published, platform linux/amd64)"
docker build -f "$repo/Dockerfile" \
  -t "$image" \
  --build-arg ADMIN_PASSWORD="$admin_password" \
  --build-arg DEMO_PASSWORD="$demo_password" \
  --platform linux/amd64 \
  "$monorepo_root"

echo "==> pushing to Artifact Registry"
gcloud auth configure-docker "$region-docker.pkg.dev" --project="$project" --quiet
docker push "$image"

echo "==> deploying to Cloud Run ($service, $region)"
# Kestrel__Endpoints__Http__Url: Cloud Run's fully-managed product terminates
# TLS at its own front end and always proxies to the container over plain
# HTTP on $PORT -- it never reaches the image's baked-in HTTPS/self-signed
# cert on :8443. ForwardedHeaders__KnownNetworks__0=0.0.0.0/0: Program.cs
# requires a trusted-proxy entry outside Development to accept
# X-Forwarded-Proto; trusting any source here is reasonable specifically
# because Cloud Run does not allow direct public access to the container --
# only Google's own front end can ever set that header.
gcloud run deploy "$service" \
  --project="$project" \
  --region="$region" \
  --image="$image" \
  --port=8080 \
  --allow-unauthenticated \
  --min-instances=0 \
  --max-instances=1 \
  --set-env-vars="Kestrel__Endpoints__Http__Url=http://+:8080,ForwardedHeaders__KnownNetworks__0=0.0.0.0/0" \
  --quiet

url="$(gcloud run services describe "$service" --project="$project" --region="$region" --format='value(status.url)')"

echo
echo "==> deployed: $url"
echo "==> sign in with either baked-in account:"
echo
echo "      DEMO (normal user, owns the sample job trees) -- published, share freely:"
echo "        username: $demo_username"
echo "        password: $demo_password"
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
