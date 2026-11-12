#!/usr/bin/env bash
# commit-gate.sh — the automated half of the AGENTS.md commit gate: restore,
# build with analyzers as errors, format check, fast core suite. Runs from the
# JobTrack folder it lives in. The targeted `dotnet test --filter` run for
# whatever the commit touches stays manual.
#
# Called by scripts/pre-commit.sh and scripts/pre-push.sh.

set -euo pipefail

# GUI and hook launches omit the user-local dotnet and Homebrew paths.
export PATH="$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin:$PATH"

readonly SOLUTION_PATH="JobTrack.slnx"
readonly RESTORE_TIMEOUT_SECONDS=180
readonly BUILD_TIMEOUT_SECONDS=300
readonly FORMAT_TIMEOUT_SECONDS=180

cd "$(dirname "${BASH_SOURCE[0]}")/.."

echo "==> dotnet restore ${SOLUTION_PATH}"
gtimeout "$RESTORE_TIMEOUT_SECONDS" dotnet restore "$SOLUTION_PATH"
echo "==> dotnet build ${SOLUTION_PATH} -warnaserror"
gtimeout "$BUILD_TIMEOUT_SECONDS" dotnet build "$SOLUTION_PATH" -warnaserror --no-restore
echo "==> dotnet format ${SOLUTION_PATH} --verify-no-changes"
gtimeout "$FORMAT_TIMEOUT_SECONDS" dotnet format "$SOLUTION_PATH" --verify-no-changes --no-restore
# The solution build above covers JobTrack.FastCore.slnf, so no --build.
./scripts/fast-test.sh
