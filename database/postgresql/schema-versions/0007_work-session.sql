-- Schema version 0007 (PostgreSQL): work_session, interval ordering,
-- active-session uniqueness, and same-user/same-leaf non-overlap. See impl
-- plan §6.2 item 7, spec §4.4, spec §11 line 630, spec_claude §3.4,
-- spike 03-gist-overlap.sql.
--
-- Rate/schedule/prerequisite tables that would otherwise reference or be
-- referenced by work_session (job_prerequisite, schedule versions, rate
-- tables) do not exist until later slices and are out of scope here.
--
-- btree_gist supplies the "=" GiST strategy for the bigint equality terms
-- (worked_by_user_id, leaf_work_id) inside the exclusion constraint below;
-- GiST's own opclasses only cover the range term natively.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE work_session
(
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leaf_work_id      bigint NOT NULL REFERENCES leaf_work (job_node_id) ON DELETE RESTRICT,
    worked_by_user_id bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    started_at        timestamptz NOT NULL,
    finished_at       timestamptz,
    -- Generated per spec line 630 ("a generated or consistently constructed
    -- tstzrange"): null finished_at (an unfinished session) is represented
    -- by an unbounded upper end so the exclusion constraint below treats an
    -- active session as overlapping any later session for the same user
    -- and leaf, without a magic "infinity" sentinel repeated at call sites.
    session_range     tstzrange GENERATED ALWAYS AS (
        tstzrange(started_at, COALESCE(finished_at, 'infinity'::timestamptz), '[)')
    ) STORED,
    changed_at        timestamptz NOT NULL DEFAULT now(),
    row_version       bigint NOT NULL DEFAULT 1,
    CONSTRAINT work_session_finished_after_started CHECK (finished_at IS NULL OR finished_at > started_at),
    -- Spec §4.4: sessions for the same user and LeafWork shall not overlap;
    -- sessions for the same user on different leaves may overlap
    -- intentionally, so leaf_work_id is a full equality term, not merely
    -- part of the range.
    CONSTRAINT work_session_no_same_leaf_user_overlap
        EXCLUDE USING gist (
            worked_by_user_id WITH =,
            leaf_work_id WITH =,
            session_range WITH &&
        )
);

CREATE INDEX work_session_leaf_work_id_idx ON work_session (leaf_work_id);
CREATE INDEX work_session_user_started_at_idx ON work_session (worked_by_user_id, started_at);
CREATE INDEX work_session_user_finished_at_idx ON work_session (worked_by_user_id, finished_at);

-- Spec §11 line 629: "a user-leading GiST overlap index ... for
-- database-wide concurrency discovery" (finding every other session
-- overlapping a given user's session, across all leaves).
CREATE INDEX work_session_user_range_gist_idx ON work_session USING gist (worked_by_user_id, session_range);

-- Spec §11 line 630: belt-and-braces alongside the exclusion constraint --
-- the spike found the exclusion constraint's concurrent-conflict path
-- surfaces as a PostgreSQL deadlock (40P01) rather than a clean exclusion
-- violation (23P01); this partial unique index gives the common "start a
-- second active session for the same user/leaf" case a plain, non-deadlocking
-- unique-violation (23505) rejection path instead.
CREATE UNIQUE INDEX work_session_one_active_per_leaf_user_idx
    ON work_session (leaf_work_id, worked_by_user_id)
    WHERE finished_at IS NULL;

-- ADR 0044: a leaf is closed to new active sessions once its achievement is
-- terminal (Success/Cancelled/Unsuccessful, ids 3/4/5 per schema version
-- 0001) or its job_node.archived_at is set; closure and session start/finish
-- must serialize against each other per leaf, not race on unlocked reads.
-- Reuses ADR 0012's lock-key construction (a fixed namespace constant XORed
-- with the contested leaf's job_node_id), added as a new lock domain: "leaf
-- session closure".
CREATE FUNCTION jobtrack_leaf_session_closure_lock_key(p_leaf_work_id bigint) RETURNS bigint AS
$$
SELECT hashtext('jobtrack:leaf-session-closure')::bigint # p_leaf_work_id;
$$ LANGUAGE sql IMMUTABLE;

-- ADR 0044 rule 1: starting (any INSERT leaving finished_at NULL) or
-- reactivating (an UPDATE leaving finished_at NULL) a session against a
-- currently closed leaf is rejected, regardless of the instant supplied --
-- current state governs. A correction that edits an already-finished
-- session's fields without reactivating it is untouched (ADR 0044 rule 5)
-- -- the WHEN clause on the UPDATE trigger below only fires when the
-- resulting row is active.
--
-- An *archived* leaf additionally rejects a brand-new row even when it is
-- already finished at insert (no operational backfill of any kind onto an
-- archived leaf); a merely terminal-achievement leaf does not -- subtree
-- import (Stage 3) legitimately inserts an already-finished historical
-- session and sets the leaf's terminal achievement inside the same
-- transaction, and PostgreSQL's deferred constraint trigger evaluates only
-- the final committed row state, not statement order, so this distinction
-- has to be encoded in the predicate itself rather than relied upon via
-- ordering.
CREATE FUNCTION check_work_session_leaf_not_closed() RETURNS trigger AS
$$
DECLARE
    v_achievement_id smallint;
    v_archived_at    timestamptz;
BEGIN
    PERFORM pg_advisory_xact_lock(jobtrack_leaf_session_closure_lock_key(NEW.leaf_work_id));

    SELECT lw.achievement_id, jn.archived_at
    INTO v_achievement_id, v_archived_at
    FROM leaf_work lw
    JOIN job_node jn ON jn.id = lw.job_node_id
    WHERE lw.job_node_id = NEW.leaf_work_id;

    IF v_archived_at IS NOT NULL OR (NEW.finished_at IS NULL AND v_achievement_id IN (3, 4, 5)) THEN
        RAISE EXCEPTION 'work_session for leaf_work % is rejected: the leaf is closed (achievement % or archived %)',
            NEW.leaf_work_id, v_achievement_id, v_archived_at
            USING ERRCODE = 'P0007';
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER work_session_leaf_not_closed_on_insert
    AFTER INSERT ON work_session
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
EXECUTE FUNCTION check_work_session_leaf_not_closed();

CREATE CONSTRAINT TRIGGER work_session_leaf_not_closed_on_update
    AFTER UPDATE OF started_at, finished_at, leaf_work_id ON work_session
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    WHEN (NEW.finished_at IS NULL)
EXECUTE FUNCTION check_work_session_leaf_not_closed();

-- ADR 0044 rule 2: a leaf_work's achievement cannot transition into a
-- terminal value (3/4/5) while any work_session on it is still active.
CREATE FUNCTION check_leaf_work_no_active_sessions_on_terminal_achievement() RETURNS trigger AS
$$
BEGIN
    IF NEW.achievement_id NOT IN (3, 4, 5) THEN
        RETURN NULL;
    END IF;

    PERFORM pg_advisory_xact_lock(jobtrack_leaf_session_closure_lock_key(NEW.job_node_id));

    IF EXISTS (SELECT 1 FROM work_session WHERE leaf_work_id = NEW.job_node_id AND finished_at IS NULL) THEN
        RAISE EXCEPTION 'leaf_work % cannot transition to a terminal achievement while a session is active',
            NEW.job_node_id
            USING ERRCODE = 'P0008';
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER leaf_work_no_active_sessions_on_terminal_achievement
    AFTER UPDATE OF achievement_id ON leaf_work
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    WHEN (NEW.achievement_id IN (3, 4, 5))
EXECUTE FUNCTION check_leaf_work_no_active_sessions_on_terminal_achievement();

-- ADR 0044 rule 3: archiving a leaf's own job_node is rejected while any
-- work_session on that leaf is still active. Archiving a branch (no
-- leaf_work attached) is unaffected.
CREATE FUNCTION check_job_node_no_active_sessions_on_archive() RETURNS trigger AS
$$
BEGIN
    IF NEW.archived_at IS NULL OR OLD.archived_at IS NOT NULL THEN
        RETURN NULL;
    END IF;

    PERFORM pg_advisory_xact_lock(jobtrack_leaf_session_closure_lock_key(NEW.id));

    IF EXISTS (
        SELECT 1
        FROM leaf_work lw
        JOIN work_session ws ON ws.leaf_work_id = lw.job_node_id
        WHERE lw.job_node_id = NEW.id AND ws.finished_at IS NULL
    ) THEN
        RAISE EXCEPTION 'job_node % cannot be archived while a session is active on its LeafWork', NEW.id
            USING ERRCODE = 'P0008';
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER job_node_no_active_sessions_on_archive
    AFTER UPDATE OF archived_at ON job_node
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    WHEN (NEW.archived_at IS NOT NULL AND OLD.archived_at IS NULL)
EXECUTE FUNCTION check_job_node_no_active_sessions_on_archive();
