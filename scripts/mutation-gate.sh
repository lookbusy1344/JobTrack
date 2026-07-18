#!/usr/bin/env bash
# Runs the mutation-testing gate (impl plan §7.5 gate item 4): a scoped
# Stryker.NET run over JobTrack.Domain's four rule-bearing categories --
# interval algebra, authorization, prerequisite/readiness, costing. See
# docs/operations/mutation-testing-gate.md for scope rationale, the recorded
# score, and the triage of every documented equivalent survivor.
#
# This is a manual gate, not a per-commit check: a full run takes several
# minutes (build + coverage analysis + per-mutant test execution). Run it once
# before closing out library-layer work, or after a change to any file under
# the config's `mutate` scope.
#
# Stryker exits nonzero when the achieved score falls below the config's
# `thresholds.break` (75), so this script gates on its own exit status -- no
# separate score parsing needed. Extra arguments are forwarded to
# dotnet-stryker (e.g. --open-report, --mutate to narrow scope further).
set -euo pipefail

# The mutate globs and test-projects path in stryker-config.json are resolved
# against the CWD, NOT the config file's location: Stryker auto-detects the
# project/solution in whatever directory it is invoked from. Running from the
# repo root would auto-detect JobTrack.slnx and mutate the entire solution
# (~215 files, >20 min) instead of this scoped ~10-min run. Always run from
# src/JobTrack.Domain/.
readonly DOMAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/src/JobTrack.Domain"
readonly CONFIG_FILE="stryker-config.json"
readonly EXPECTED_STRYKER_VERSION="4.16.0"

if ! command -v dotnet-stryker >/dev/null 2>&1; then
	echo "dotnet-stryker not found on PATH." >&2
	echo "Install the pinned global tool (see docs/operations/global-tools.md):" >&2
	echo "  dotnet tool install --global dotnet-stryker --version ${EXPECTED_STRYKER_VERSION}" >&2
	exit 1
fi

# The recorded score is version-sensitive. dotnet-stryker has no --version flag; it prints its
# version in the run banner ("Version: x.y.z") below. Confirm that matches EXPECTED_STRYKER_VERSION
# and, on a bump, re-record the score in docs/operations/mutation-testing-gate.md.

cd "$DOMAIN_DIR"
echo "==> dotnet-stryker --config-file ${CONFIG_FILE} (from ${DOMAIN_DIR})"
exec dotnet-stryker --config-file "$CONFIG_FILE" "$@"
