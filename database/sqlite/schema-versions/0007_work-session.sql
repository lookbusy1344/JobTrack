-- Schema version 0007 (SQLite): work_session, interval ordering,
-- active-session uniqueness, and same-user/same-leaf non-overlap. See impl
-- plan §6.2 item 7, spec §4.4, spec §11 line 652, spec_claude §3.4,
-- ADR 0007.
--
-- SQLite has no GiST exclusion constraints, so same-user/same-leaf overlap
-- is enforced by immediate triggers rather than a declarative range
-- constraint. Half-open interval overlap for two sessions [s1, e1) and
-- [s2, e2), each with a nullable (open/unbounded) end, is:
--   (e2 IS NULL OR s1 < e2) AND (e1 IS NULL OR s2 < e1)
-- -- expressed this way (rather than substituting a sentinel "infinity"
-- tick value for a null end) to avoid a magic-number stand-in for
-- unbounded.

CREATE TABLE work_session
(
    id                INTEGER PRIMARY KEY,
    leaf_work_id      INTEGER NOT NULL REFERENCES leaf_work (job_node_id) ON DELETE RESTRICT,
    worked_by_user_id INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    started_at        INTEGER NOT NULL,
    finished_at       INTEGER,
    changed_at        INTEGER NOT NULL,
    row_version       INTEGER NOT NULL DEFAULT 1,
    CHECK (finished_at IS NULL OR finished_at > started_at)
) STRICT;

CREATE INDEX work_session_leaf_work_id_idx ON work_session (leaf_work_id);
CREATE INDEX work_session_user_started_at_idx ON work_session (worked_by_user_id, started_at);
CREATE INDEX work_session_user_finished_at_idx ON work_session (worked_by_user_id, finished_at);

-- At most one active (unfinished) session per (leaf_work_id, worked_by_user_id).
CREATE UNIQUE INDEX work_session_one_active_per_leaf_user_idx
    ON work_session (leaf_work_id, worked_by_user_id)
    WHERE finished_at IS NULL;

CREATE TRIGGER work_session_no_same_leaf_user_overlap_on_insert
    AFTER INSERT
    ON work_session
BEGIN
    SELECT RAISE(ABORT, 'overlapping work session for the same user and leaf work')
    WHERE EXISTS (
        SELECT 1
        FROM work_session ws
        WHERE ws.id <> NEW.id
          AND ws.leaf_work_id = NEW.leaf_work_id
          AND ws.worked_by_user_id = NEW.worked_by_user_id
          AND (ws.finished_at IS NULL OR NEW.started_at < ws.finished_at)
          AND (NEW.finished_at IS NULL OR ws.started_at < NEW.finished_at)
    );
END;

CREATE TRIGGER work_session_no_same_leaf_user_overlap_on_update
    AFTER UPDATE OF started_at, finished_at, leaf_work_id, worked_by_user_id
    ON work_session
BEGIN
    SELECT RAISE(ABORT, 'overlapping work session for the same user and leaf work')
    WHERE EXISTS (
        SELECT 1
        FROM work_session ws
        WHERE ws.id <> NEW.id
          AND ws.leaf_work_id = NEW.leaf_work_id
          AND ws.worked_by_user_id = NEW.worked_by_user_id
          AND (ws.finished_at IS NULL OR NEW.started_at < ws.finished_at)
          AND (NEW.finished_at IS NULL OR ws.started_at < NEW.finished_at)
    );
END;
