#!/usr/bin/env bash
# Two opposing-order move requests: session A moves (10, 20), session B
# moves (20, 10) — same pair of nodes, opposite argument order. Without
# deterministic lock-key ordering this deadlocks; with it (both acquire
# key 10 before key 20 regardless of argument order), both complete via
# serialization.
set -uo pipefail
DB="jobtrack_spike"
USER="$(whoami)"
OUT_DIR="$(mktemp -d)"

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 -tAc "SELECT move_node_locked(10, 20);" \
    > "$OUT_DIR/a.out" 2>&1
) &
PID_A=$!

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 -tAc "SELECT move_node_locked(20, 10);" \
    > "$OUT_DIR/b.out" 2>&1
) &
PID_B=$!

RC_A=0; wait "$PID_A" || RC_A=$?
RC_B=0; wait "$PID_B" || RC_B=$?

echo "--- session A (rc=$RC_A) ---"; cat "$OUT_DIR/a.out"
echo "--- session B (rc=$RC_B) ---"; cat "$OUT_DIR/b.out"

if [ "$RC_A" -eq 0 ] && [ "$RC_B" -eq 0 ]; then
  echo "RESULT: PASS — both moves completed via serialization, no deadlock"
else
  echo "RESULT: FAIL — deadlock or error under opposing-order requests"
  exit 1
fi

rm -rf "$OUT_DIR"
