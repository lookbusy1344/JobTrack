#!/usr/bin/env bash
# Two independent connections concurrently insert A->B and B->A. Neither
# edge is individually a cycle against committed data, but together they
# are. Runs the naive (unlocked) path first to show the deferred-CTE
# trigger alone misses the race, then the locked path to show the
# advisory-lock mitigation (ADR 0012) closes it.
set -uo pipefail
DB="jobtrack_spike"
USER="$(whoami)"
OUT_DIR="$(mktemp -d)"
sync_delay=1

run_case () {
  local label="$1" insert_a="$2" insert_b="$3"
  psql -U "$USER" -d "$DB" -c "TRUNCATE job_prerequisite;" >/dev/null

  (
    psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/a.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
$insert_a
COMMIT;
SQL
  ) &
  local pid_a=$!

  (
    psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/b.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
$insert_b
COMMIT;
SQL
  ) &
  local pid_b=$!

  local rc_a=0 rc_b=0
  wait "$pid_a" || rc_a=$?
  wait "$pid_b" || rc_b=$?

  echo "=== $label ==="
  echo "--- session A (rc=$rc_a) ---"; cat "$OUT_DIR/a.out"
  echo "--- session B (rc=$rc_b) ---"; cat "$OUT_DIR/b.out"

  local cycle_present
  cycle_present=$(psql -U "$USER" -d "$DB" -tAc \
    "SELECT EXISTS (SELECT 1 FROM job_prerequisite a JOIN job_prerequisite b ON a.from_id = b.to_id AND a.to_id = b.from_id);")
  echo "cycle present after both commits: $cycle_present"
  echo
}

run_case "unlocked (naive INSERT, deferred CTE trigger only)" \
  "INSERT INTO job_prerequisite (from_id, to_id) VALUES (1, 2);" \
  "INSERT INTO job_prerequisite (from_id, to_id) VALUES (2, 1);"

run_case "locked (add_prerequisite_edge_locked, ADR 0012 mitigation)" \
  "SELECT add_prerequisite_edge_locked(1, 2);" \
  "SELECT add_prerequisite_edge_locked(2, 1);"

rm -rf "$OUT_DIR"
