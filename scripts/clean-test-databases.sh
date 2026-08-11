#!/usr/bin/env bash
# Drops orphaned jobtrack_test_* PostgreSQL databases and deletes orphaned
# jobtrack_test_*.db SQLite files left behind by a killed/interrupted
# `dotnet test` run (timeout, Ctrl-C, crashed sandboxed process). Both test
# fixtures normally clean up after themselves on DisposeAsync; this only
# matters when that didn't happen. Continues past individual failures so one
# bad drop/delete doesn't hide the rest of the cleanup -- failures are
# collected and reported, with a nonzero exit, at the end.
set -euo pipefail

PGHOST="${PGHOST:-/tmp}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-$(whoami)}"
PGADMIN_DATABASE="${PGADMIN_DATABASE:-postgres}"

failures=()

echo "Looking for orphaned PostgreSQL test databases on ${PGHOST}:${PGPORT} (user ${PGUSER})..."

if ! command -v psql >/dev/null 2>&1; then
	echo "psql not found on PATH; skipping PostgreSQL cleanup." >&2
	failures+=("psql not found on PATH")
else
	db_list=""
	if ! db_list=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGADMIN_DATABASE" -Atc \
		"SELECT datname FROM pg_database WHERE datname LIKE 'jobtrack_test_%';"); then
		echo "Failed to query PostgreSQL for orphaned test databases (is the server reachable?)." >&2
		failures+=("PostgreSQL query failed")
	fi

	databases=()
	while IFS= read -r db; do
		[[ -n "$db" ]] && databases+=("$db")
	done <<< "$db_list"

	if [[ ${#databases[@]} -eq 0 ]]; then
		echo "No orphaned PostgreSQL test databases found."
	else
		for db in "${databases[@]}"; do
			echo "Dropping PostgreSQL database: $db"
			if ! psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGADMIN_DATABASE" -c "DROP DATABASE \"$db\" WITH (FORCE);"; then
				echo "Failed to drop PostgreSQL database: $db" >&2
				failures+=("drop PostgreSQL database $db")
			fi
		done
	fi
fi

sqlite_dir="${TMPDIR:-/tmp}"
echo "Looking for orphaned SQLite test database files in ${sqlite_dir}..."

shopt -s nullglob
sqlite_files=(
	"$sqlite_dir"/jobtrack_test_*.db
	"$sqlite_dir"/jobtrack_test_*.db-shm
	"$sqlite_dir"/jobtrack_test_*.db-wal
	"$sqlite_dir"/jobtrack_test_*.db-journal
)
shopt -u nullglob

if [[ ${#sqlite_files[@]} -eq 0 ]]; then
	echo "No orphaned SQLite test database files found."
else
	for f in "${sqlite_files[@]}"; do
		echo "Removing SQLite test file: $f"
		if ! rm -f "$f"; then
			echo "Failed to remove SQLite test file: $f" >&2
			failures+=("remove SQLite file $f")
		fi
	done
fi

if [[ ${#failures[@]} -gt 0 ]]; then
	echo "" >&2
	echo "Cleanup completed with ${#failures[@]} failure(s):" >&2
	for f in "${failures[@]}"; do
		echo "  - $f" >&2
	done
	exit 1
fi

echo "Cleanup complete."
