#!/usr/bin/env bash
# Runs the fast core test suite: the projects with no PostgreSQL, web-host, or
# browser dependency (see README's "Fast core suite" section). Sub-20s. Part of
# the per-commit gate, alongside build, format, and a targeted `dotnet test
# --filter` run against whatever the commit touches (see CLAUDE.md).
#
# --longer/-l additionally runs the highest-value PostgreSQL-backed and
# web-integration projects (contract enforcement, provider-specific
# concurrency, host wiring) for a sub-80s check -- still short of the full
# `dotnet test JobTrack.slnx` (several minutes, all providers + browser e2e).
#
# §2.4 of the 2026-07-28 fresh-eyes review: `--build` used to run
# `dotnet test <project>` (no `--no-build`) once per project, so each of the
# seven-to-ten projects separately restored and built the whole dependency
# graph it shares with every other project here -- repeated CLI/MSBuild
# startup and restore/build checks, not test execution, accounted for most of
# the wall time. `--build` now builds `JobTrack.FastCore.slnf` (this suite's
# own dependency closure, not the whole solution) exactly once, then every
# project runs with `--no-build --no-restore`.
#
# The budget stays informational by default (prints a warning, still exits 0)
# -- this is the interactive/local mode CLAUDE.md's commit gate treats as
# authoritative, since a machine running slower under incidental load must
# never block an otherwise-passing commit (the same lesson §2.3's
# performance-lane fix already drew: a flaky pass/fail gate is worse than an
# honest warning). Pass --strict for a CI-style mode that exits non-zero on a
# real overrun -- use this where a human is not present to read the warning.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

readonly FAST_CORE_SLNF="JobTrack.FastCore.slnf"
readonly FAST_TEST_TIMEOUT_SECONDS=30
readonly LONGER_TEST_TIMEOUT_SECONDS=60
readonly BUILD_TIMEOUT_SECONDS=120
readonly FAST_BUDGET_SECONDS=20
readonly LONGER_BUDGET_SECONDS=80
readonly FAST_PROJECTS=(
	tests/JobTrack.Domain.Tests
	tests/JobTrack.Application.Tests
	tests/JobTrack.ArchitectureTests
	tests/JobTrack.Identity.Tests
	tests/JobTrack.Persistence.Shared.Tests
	tests/JobTrack.Persistence.Sqlite.Tests
	tests/JobTrack.PublicApi.Tests
)
readonly LONGER_PROJECTS=(
	tests/JobTrack.Database.ContractTests
	tests/JobTrack.Persistence.PostgreSql.Tests
	tests/JobTrack.Web.IntegrationTests
)

skip_build=1
longer=0
strict=0
for arg in "$@"; do
	case "$arg" in
		--build) skip_build=0 ;;
		--longer|-l) longer=1 ;;
		--strict) strict=1 ;;
	esac
done

if [[ "$longer" -eq 1 ]]; then
	suite_name="Longer"
	budget_seconds=$LONGER_BUDGET_SECONDS
	projects=("${FAST_PROJECTS[@]}" "${LONGER_PROJECTS[@]}")
else
	suite_name="Fast core"
	budget_seconds=$FAST_BUDGET_SECONDS
	projects=("${FAST_PROJECTS[@]}")
fi

start_seconds=$SECONDS
budget_exceeded=0

report_duration() {
	local exit_code=$?
	local elapsed=$((SECONDS - start_seconds))
	echo "${suite_name} suite took ${elapsed}s (budget: ${budget_seconds}s)."
	if [[ "$elapsed" -gt "$budget_seconds" ]]; then
		budget_exceeded=1
		echo "Warning: exceeded the ${budget_seconds}s budget -- see README's \"Fast core suite\" section." >&2
	fi
	if [[ "$exit_code" -eq 0 && "$budget_exceeded" -eq 1 && "$strict" -eq 1 ]]; then
		echo "Failing: --strict mode treats a budget overrun as a suite failure." >&2
		exit 1
	fi
}
trap report_duration EXIT

is_longer_project() {
	local candidate="$1"
	local longer_project
	for longer_project in "${LONGER_PROJECTS[@]}"; do
		if [[ "$candidate" == "$longer_project" ]]; then
			return 0
		fi
	done
	return 1
}

if [[ "$skip_build" -eq 0 ]]; then
	dotnet build-server shutdown
	echo "==> dotnet build ${FAST_CORE_SLNF}"
	gtimeout "$BUILD_TIMEOUT_SECONDS" dotnet build "$FAST_CORE_SLNF"
fi

for project in "${projects[@]}"; do
	echo "==> dotnet test ${project}"
	if is_longer_project "$project"; then
		timeout_seconds=$LONGER_TEST_TIMEOUT_SECONDS
	else
		timeout_seconds=$FAST_TEST_TIMEOUT_SECONDS
	fi
	gtimeout "$timeout_seconds" dotnet test "$project" --no-build
done

echo "${suite_name} suite passed."
