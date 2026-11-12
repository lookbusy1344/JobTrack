#!/usr/bin/env bash
# pre-commit.sh — run scripts/commit-gate.sh when the pending change touches
# JobTrack code. Works in jj and plain Git checkouts of the monorepo.
#
# Only files under JobTrack/ count: a change confined to a sibling project
# passes untouched, as does a documentation-only change (*.md, docs/).
#
# jj: no commit hooks, so run it before `jj commit`, `jj describe`
# (finalising) and `jj squash`. It checks the files changed in `@`.
#
# Git: the hooks folder is shared by the whole monorepo. From the repo root:
#   ln -sf ../../JobTrack/scripts/pre-commit.sh .git/hooks/pre-commit
# It checks the files changed against HEAD.

set -euo pipefail

export PATH="$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin:$PATH"

is_documentation_file() {
	local path="$1"

	[[ "$path" == *.md ]] || [[ "$path" == docs/* ]]
}

# Paths relative to the JobTrack folder, limited to it.
changed_files() {
	if [[ -z "${GIT_INDEX_FILE:-}" ]] && jj --ignore-working-copy root > /dev/null 2>&1; then
		jj diff --name-only -r @ --no-pager -- .
	else
		git diff HEAD --name-only --relative -- .
	fi
}

main() {
	cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/.."

	local files=()
	local file

	while IFS= read -r file; do
		[[ -n "$file" ]] && files+=("$file")
	done < <(changed_files)

	if [[ "${#files[@]}" -eq 0 ]]; then
		echo "No JobTrack files changed. Skipping checks."
		exit 0
	fi

	for file in "${files[@]}"; do
		if ! is_documentation_file "$file"; then
			echo "Running the JobTrack commit gate for ${#files[@]} changed file(s)..."
			exec ./scripts/commit-gate.sh
		fi
	done

	echo "Documentation-only change. Skipping checks."
}

main "$@"
