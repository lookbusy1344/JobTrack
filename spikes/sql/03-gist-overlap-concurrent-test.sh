#!/usr/bin/env bash
# Two independent connections concurrently try to start an open (unbounded
# upper) session for the same user/leaf. Expected: exactly one succeeds,
# the other observes an exclusion-constraint (or unique-index) violation.
set -uo pipefail
DB="jobtrack_spike"
USER="$(whoami)"
OUT_DIR="$(mktemp -d)"
sync_delay=1

psql -U "$USER" -d "$DB" -c "DELETE FROM work_session WHERE user_id = 2;" >/dev/null

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/a.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
INSERT INTO work_session (user_id, leaf_id, session_range) VALUES (2, 200, tstzrange('2026-01-01 09:00+00', NULL, '[)'));
COMMIT;
SQL
) &
PID_A=$!

(
  psql -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 <<SQL > "$OUT_DIR/b.out" 2>&1
BEGIN;
SELECT pg_sleep($sync_delay);
INSERT INTO work_session (user_id, leaf_id, session_range) VALUES (2, 200, tstzrange('2026-01-01 09:05+00', NULL, '[)'));
COMMIT;
SQL
) &
PID_B=$!

RC_A=0; wait "$PID_A" || RC_A=$?
RC_B=0; wait "$PID_B" || RC_B=$?

echo "--- session A (rc=$RC_A) ---"; cat "$OUT_DIR/a.out"
echo "--- session B (rc=$RC_B) ---"; cat "$OUT_DIR/b.out"

OPEN_COUNT=$(psql -U "$USER" -d "$DB" -tAc "SELECT count(*) FROM work_session WHERE user_id = 2 AND leaf_id = 200 AND upper_inf(session_range);")
echo "final open-session count for (user=2, leaf=200): $OPEN_COUNT"

if [ "$OPEN_COUNT" -eq 1 ] && [ "$RC_A" != "$RC_B" ]; then
  echo "RESULT: PASS — exactly one open session, exactly one session rejected"
else
  echo "RESULT: FAIL"
  exit 1
fi

rm -rf "$OUT_DIR"
