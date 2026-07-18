#!/usr/bin/env bash
# Runs the fast core test suite: the projects with no PostgreSQL, web-host, or
# browser dependency (see README's "Fast core suite" section). Intended as a
# sub-20s sanity check between edits -- it does not replace the full commit
# gate (`dotnet build/format/test JobTrack.slnx`) required before a commit.
#
# --longer/-l additionally runs the highest-value PostgreSQL-backed and
# web-integration projects (contract enforcement, provider-specific
# concurrency, host wiring) for a sub-80s check -- still short of the full
# `dotnet test JobTrack.slnx` (several minutes, all providers + browser e2e).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

readonly FAST_TEST_TIMEOUT_SECONDS=30
readonly LONGER_TEST_TIMEOUT_SECONDS=60
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
for arg in "$@"; do
	case "$arg" in
		--build) skip_build=0 ;;
		--longer|-l) longer=1 ;;
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

report_duration() {
	local elapsed=$((SECONDS - start_seconds))
	echo "${suite_name} suite took ${elapsed}s (budget: ${budget_seconds}s)."
	if [[ "$elapsed" -gt "$budget_seconds" ]]; then
		echo "Warning: exceeded the ${budget_seconds}s budget -- see README's \"Fast core suite\" section." >&2
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

for project in "${projects[@]}"; do
	echo "==> dotnet test ${project}"
	if is_longer_project "$project"; then
		timeout_seconds=$LONGER_TEST_TIMEOUT_SECONDS
	else
		timeout_seconds=$FAST_TEST_TIMEOUT_SECONDS
	fi
	if [[ "$skip_build" -eq 1 ]]; then
		gtimeout "$timeout_seconds" dotnet test "$project" --no-build
	else
		gtimeout "$timeout_seconds" dotnet test "$project"
	fi
done

echo "${suite_name} suite passed."
