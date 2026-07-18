#!/usr/bin/env bash
# Same race as 01-single-root-concurrent-test.sh, against the count-only
# counterfactual schema. Expected (and this is the point): BOTH inserts
# succeed, leaving 2 roots — the count-based deferred trigger cannot see
# the other transaction's uncommitted row at check time, so each
# transaction's own count check passes independently.
set -uo pipefail
DB="jobtrack_spike"
USER="$(whoami)"
OUT_DIR="$(mktemp -d)"

psql -U "$USER" -d "$DB" -c "TRUNCATE job_node_counterfactual RESTART IDENTITY;" >/dev/null

sync_delay=1

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/a.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
INSERT INTO job_node_counterfactual (parent_id) VALUES (NULL);
COMMIT;
SQL
) &
PID_A=$!

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/b.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
INSERT INTO job_node_counterfactual (parent_id) VALUES (NULL);
COMMIT;
SQL
) &
PID_B=$!

RC_A=0; wait "$PID_A" || RC_A=$?
RC_B=0; wait "$PID_B" || RC_B=$?

echo "--- session A (rc=$RC_A) ---"; cat "$OUT_DIR/a.out"
echo "--- session B (rc=$RC_B) ---"; cat "$OUT_DIR/b.out"

ROOT_COUNT=$(psql -U "$USER" -d "$DB" -tAc "SELECT count(*) FROM job_node_counterfactual WHERE parent_id IS NULL;")
echo "final root count: $ROOT_COUNT"

if [ "$ROOT_COUNT" -eq 2 ] && [ "$RC_A" -eq 0 ] && [ "$RC_B" -eq 0 ]; then
  echo "RESULT: CONFIRMED COUNTERFACTUAL FAILURE — count-only check let 2 roots through"
else
  echo "RESULT: counterfactual did not reproduce as expected (root count=$ROOT_COUNT, rc_a=$RC_A, rc_b=$RC_B)"
fi

rm -rf "$OUT_DIR"
