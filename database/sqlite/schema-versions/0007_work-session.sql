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

-- ADR 0044: a leaf is closed to new active sessions once its achievement is
-- terminal (Success/Cancelled/Unsuccessful, ids 3/4/5 per schema version
-- 0001) or its job_node.archived_at is set. SQLite has no advisory lock;
-- the persistence layer's existing BEGIN IMMEDIATE/single-writer
-- serialization (impl plan §7.4) is what makes this check race-free against
-- a concurrent closure, matching the same-user/same-leaf overlap triggers
-- above.
--
-- Rule 1: starting (any INSERT leaving finished_at NULL) or reactivating (an
-- UPDATE leaving finished_at NULL) a session against a currently closed leaf
-- is rejected. A correction that edits an already-finished session's fields
-- without reactivating it is untouched (ADR 0044 rule 5) -- the UPDATE
-- trigger below only fires when the resulting row is active.
--
-- An *archived* leaf additionally rejects a brand-new row even when it is
-- already finished at insert (no operational backfill onto an archived
-- leaf); a merely terminal-achievement leaf does not -- subtree import
-- (Stage 3) legitimately inserts an already-finished historical session and
-- sets the leaf's terminal achievement inside the same transaction.
CREATE TRIGGER work_session_leaf_not_closed_on_insert
    AFTER INSERT
    ON work_session
BEGIN
    SELECT RAISE(ABORT, 'work-session-leaf-closed')
    WHERE EXISTS (
        SELECT 1
        FROM leaf_work lw
        JOIN job_node jn ON jn.id = lw.job_node_id
        WHERE lw.job_node_id = NEW.leaf_work_id
          AND (jn.archived_at IS NOT NULL OR (NEW.finished_at IS NULL AND lw.achievement_id IN (3, 4, 5)))
    );
END;

CREATE TRIGGER work_session_leaf_not_closed_on_update
    AFTER UPDATE OF started_at, finished_at, leaf_work_id
    ON work_session
    WHEN NEW.finished_at IS NULL
BEGIN
    SELECT RAISE(ABORT, 'work-session-leaf-closed')
    WHERE EXISTS (
        SELECT 1
        FROM leaf_work lw
        JOIN job_node jn ON jn.id = lw.job_node_id
        WHERE lw.job_node_id = NEW.leaf_work_id
          AND (lw.achievement_id IN (3, 4, 5) OR jn.archived_at IS NOT NULL)
    );
END;

-- Rule 2: leaf_work cannot transition into a terminal achievement while any
-- work_session on it is still active.
CREATE TRIGGER leaf_work_no_active_sessions_on_terminal_achievement
    AFTER UPDATE OF achievement_id
    ON leaf_work
    WHEN NEW.achievement_id IN (3, 4, 5)
BEGIN
    SELECT RAISE(ABORT, 'leaf-closure-active-sessions')
    WHERE EXISTS (SELECT 1 FROM work_session WHERE leaf_work_id = NEW.job_node_id AND finished_at IS NULL);
END;

-- Rule 3: a leaf's own job_node cannot be archived while any work_session on
-- it is still active. Archiving a branch (no leaf_work attached) is
-- unaffected.
CREATE TRIGGER job_node_no_active_sessions_on_archive
    AFTER UPDATE OF archived_at
    ON job_node
    WHEN NEW.archived_at IS NOT NULL AND OLD.archived_at IS NULL
BEGIN
    SELECT RAISE(ABORT, 'leaf-closure-active-sessions')
    WHERE EXISTS (
        SELECT 1
        FROM leaf_work lw
        JOIN work_session ws ON ws.leaf_work_id = lw.job_node_id
        WHERE lw.job_node_id = NEW.id AND ws.finished_at IS NULL
    );
END;
