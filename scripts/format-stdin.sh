#!/usr/bin/env bash
# format-stdin.sh <path> — `jj fix` tool: read C# source on stdin, write the
# whitespace-formatted result to stdout. <path> is workspace-relative.
#
# `dotnet format` works on files, so the input goes through a scratch tree
# that mirrors every .editorconfig on the way from the workspace root to
# <path>. On a formatter failure the input passes through unchanged; the
# `dotnet format --verify-no-changes` step in commit-gate.sh still catches it.

set -euo pipefail

export PATH="$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin:$PATH"

readonly EDITORCONFIG=".editorconfig"

path="${1:?usage: format-stdin.sh <path>}"

workspace_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly workspace_root

scratch="$(mktemp -d)"
trap 'rm -rf "${scratch}"' EXIT

copy_editorconfig() {
	local relative="$1"

	if [[ -f "${workspace_root}/${relative}/${EDITORCONFIG}" ]]; then
		mkdir -p "${scratch}/${relative}"
		cp "${workspace_root}/${relative}/${EDITORCONFIG}" "${scratch}/${relative}/"
	fi
}

relative="."
copy_editorconfig "$relative"
IFS=/ read -ra parts <<< "$(dirname "$path")"
for part in "${parts[@]}"; do
	relative="${relative}/${part}"
	copy_editorconfig "$relative"
done

mkdir -p "${scratch}/$(dirname "$path")"
cat > "${scratch}/${path}"

dotnet format whitespace "${scratch}" --folder --include "${path}" > /dev/null 2>&1 || true
cat "${scratch}/${path}"
