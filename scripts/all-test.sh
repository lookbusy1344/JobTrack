#!/usr/bin/env bash
# Runs everything: the full solution suite (every project `dotnet test JobTrack.slnx` reaches) plus
# JobTrack.Database.PerformanceTests, which deliberately sets IsTestProject=false so the solution-wide
# run above silently skips it (the developer guide's "Performance lane", §2.3 of the 2026-07-28 fresh-eyes review --
# that project's ceilings must never be measured or enforced under this script's own cross-project
# contention). This script is the one command that covers both; neither half is a substitute for the
# other. Takes several minutes -- for occasional use (end of a multi-stage plan, before a substantial
# closing commit), not the per-commit gate (see CLAUDE.md and the developer guide's "Fast core suite").
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

readonly FULL_SUITE_TIMEOUT_SECONDS=600

cleanup_test_databases() {
	./scripts/clean-test-databases.sh
}

trap cleanup_test_databases EXIT

cleanup_test_databases
dotnet build-server shutdown
gtimeout "$FULL_SUITE_TIMEOUT_SECONDS" dotnet test JobTrack.slnx
cleanup_test_databases

./scripts/perf-test.sh "$@"

echo "All tests (full solution suite + performance lane) passed."
