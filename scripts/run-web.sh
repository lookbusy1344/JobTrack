#!/usr/bin/env bash
#
# Watch, build, and launch JobTrack.Web against the persistent local "live"
# PostgreSQL instance (jobtrack_live) for manual testing.
# Rebuilds and restarts automatically whenever a source file changes.
#
# appsettings.Development.json ships pointed at the disposable SQLite dev
# database. The "https (jobtrack_live)" launch profile carries the PostgreSQL
# provider/connection-string overrides, so this script and a Rider debug session
# run against identical settings — see docs/operations/local-live-instance.md.
#
# That profile is https-only for a reason: Program.cs sets
# Cookie.SecurePolicy = CookieSecurePolicy.Always, so over plain HTTP the
# browser silently discards the auth cookie and every request bounces back
# to /Account/Login with no visible error.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
project="$repo/src/JobTrack.Web/JobTrack.Web.csproj"

echo "==> watching JobTrack.Web against jobtrack_live on https://localhost:7174 (rebuilds on change)"
dotnet watch run --project "$project" --launch-profile "https (jobtrack_live)" --non-interactive
