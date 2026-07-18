#!/usr/bin/env bash
# Two independent connections race to insert the first root into an empty
# job_node table. Expected: exactly one succeeds, the other observes a
# unique-violation on idx_job_node_single_root. Proves the partial unique
# index resolves the race that a count-based deferred check alone cannot
# (see spike-report.md for the count-based failure mode).
set -euo pipefail
DB="jobtrack_spike"
USER="$(whoami)"
OUT_DIR="$(mktemp -d)"

psql -U "$USER" -d "$DB" -c "TRUNCATE job_node RESTART IDENTITY;" >/dev/null

sync_delay=1

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/a.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
INSERT INTO job_node (parent_id, is_leaf) VALUES (NULL, false);
COMMIT;
SQL
) &
PID_A=$!

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/b.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
INSERT INTO job_node (parent_id, is_leaf) VALUES (NULL, false);
COMMIT;
SQL
) &
PID_B=$!

RC_A=0; wait "$PID_A" || RC_A=$?
RC_B=0; wait "$PID_B" || RC_B=$?

echo "--- session A (rc=$RC_A) ---"; cat "$OUT_DIR/a.out"
echo "--- session B (rc=$RC_B) ---"; cat "$OUT_DIR/b.out"

ROOT_COUNT=$(psql -U "$USER" -d "$DB" -tAc "SELECT count(*) FROM job_node WHERE parent_id IS NULL;")
echo "final root count: $ROOT_COUNT"

if [ "$ROOT_COUNT" -eq 1 ] && { [ "$RC_A" -ne 0 ] || [ "$RC_B" -ne 0 ]; } && [ "$RC_A" != "$RC_B" ]; then
  echo "RESULT: PASS — exactly one root, exactly one session rejected"
else
  echo "RESULT: FAIL — expected exactly one success and one unique-violation rejection"
  exit 1
fi

rm -rf "$OUT_DIR"
