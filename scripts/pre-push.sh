#!/usr/bin/env bash
# pre-push.sh — format and check the unpushed revisions that touch JobTrack/.
#
# jj: the stack is every non-empty mutable revision in ::@ that changes a file
# under JobTrack/. Revisions confined to sibling projects are ignored.
# Default: the tip only (the newest revision in the stack), checked in the
# working copy. --full: every revision in the stack, each in its own checkout
# via `jj run`. Both first apply whitespace formatting to JobTrack/**/*.cs with
# `jj fix` (scripts/format-stdin.sh).
#
# Git: runs the checks on the working tree without formatting it, and refuses
# uncommitted changes to tracked JobTrack files so the checked tree is the
# pushed HEAD. Skips when no commit since the upstream touches JobTrack/.
# --full makes no difference there.
#
# The checks are scripts/commit-gate.sh.

set -euo pipefail

export PATH="$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin:$PATH"

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly PROJECT_DIR
PROJECT_SUBDIR="$(basename "${PROJECT_DIR}")"
readonly PROJECT_SUBDIR
readonly STACK="mutable() & ::@ ~ empty() & files(root:\"${PROJECT_SUBDIR}\")"
readonly TIP="heads(${STACK})"
readonly CHECKS="scripts/commit-gate.sh"

usage() {
	echo "Usage: $0 [--full|-f]"
	echo "  --full, -f  Format and check every revision in the stack, not only the tip."
}

full=false
while [[ $# -gt 0 ]]; do
	case "$1" in
		--full | -f) full=true; shift ;;
		-h | --help) usage; exit 0 ;;
		*) echo "Error: unknown argument '$1'" >&2; usage >&2; exit 1 ;;
	esac
done

cd "${PROJECT_DIR}"

if jj --ignore-working-copy root > /dev/null 2>&1; then
	use_jj=true
elif git rev-parse --show-toplevel > /dev/null 2>&1; then
	use_jj=false
else
	echo "==> Not a jj or Git repository, skipping pre-push checks."
	exit 0
fi

if [[ "${use_jj}" == false ]]; then
	if [[ -n "$(git status --porcelain --untracked-files=no -- .)" ]]; then
		echo "==> Uncommitted changes to tracked JobTrack files. Commit or stash them first." >&2
		exit 1
	fi
	if git rev-parse --verify --quiet '@{upstream}' > /dev/null \
		&& [[ -z "$(git diff --name-only '@{upstream}' HEAD -- .)" ]]; then
		echo "==> No unpushed JobTrack changes, nothing to check."
		exit 0
	fi
	echo "==> Checking working tree at $(git log -1 --format='%h %s')"
	"./${CHECKS}"
	echo "==> All checks passed."
	exit 0
fi

# Absolute tool path: `jj fix` does not promise the tool's working directory.
readonly FORMAT_CONFIG=(
	--config "fix.tools.dotnet-format.command=[\"${PROJECT_DIR}/scripts/format-stdin.sh\", \"\$path\"]"
	--config "fix.tools.dotnet-format.patterns=[\"root-glob:\\\"${PROJECT_SUBDIR}/**/*.cs\\\"\"]"
	--config 'fix.tools.dotnet-format.enabled=true'
)

stack="$(jj log --no-graph -r "${STACK}" -T 'change_id ++ "\n"')"
if [[ -z "${stack}" ]]; then
	echo "==> No unpushed JobTrack revisions, nothing to check."
	exit 0
fi

if [[ "${full}" == true ]]; then
	echo "==> Formatting ${STACK}"
	jj "${FORMAT_CONFIG[@]}" fix -s "roots(${STACK})"
	echo "==> Checking each revision in ${STACK}"
	# Without --root, `jj run` starts in this subdirectory of each checkout.
	jj run --ignore-changes -r "${STACK}" -- "${CHECKS}"
else
	# Revisions above the tip leave JobTrack/ alone, so the working copy's
	# JobTrack/ tree is the tip's.
	echo "==> Formatting tip ${TIP}"
	jj "${FORMAT_CONFIG[@]}" fix -s "${TIP}"
	echo "==> Checking tip $(jj log --no-graph -r "${TIP}" -T 'change_id.short() ++ " " ++ coalesce(description.first_line(), "(no description)")')"
	"./${CHECKS}"
fi

echo "==> All checks passed."
