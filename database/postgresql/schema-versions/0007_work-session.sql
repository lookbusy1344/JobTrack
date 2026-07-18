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
