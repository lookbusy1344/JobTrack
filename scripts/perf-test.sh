#!/usr/bin/env bash
# §2.3 of the 2026-07-28 fresh-eyes review: the one deterministic performance
# lane for JobTrack.Database.PerformanceTests. Every latency ceiling in
# docs/traceability/performance-budgets.md is set against THIS invocation --
# alone, serialized, with orphaned test databases cleaned before and after --
# never against a `dotnet test JobTrack.slnx` run where other PostgreSQL-backed
# projects contend for the same local instance concurrently. That contention is
# real (roughly 2-3x isolated latency, measured 2026-07-28) but it is a runner
# scheduling concern, not a query regression, so it must never widen a ceiling
# here (CLAUDE.md's "commit gate" section and this file's own header both say
# so). The project sets IsTestProject=false so `dotnet test JobTrack.slnx` (and
# any full/broad solution run) silently skips it rather than ever failing on
# contention -- the full suite must always be able to pass. This script
# overrides that back to true for its own deliberate, direct invocation; it is
# the only supported way to run this project's tests.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

readonly PERF_TEST_TIMEOUT_SECONDS=600

cleanup_test_databases() {
	./scripts/clean-test-databases.sh
}

trap cleanup_test_databases EXIT

cleanup_test_databases
gtimeout "$PERF_TEST_TIMEOUT_SECONDS" dotnet test tests/JobTrack.Database.PerformanceTests -p:IsTestProject=true "$@"
