-- Spike 3: GiST exclusion constraint for same-user/same-leaf session
-- overlap, including the unbounded-upper tstzrange for open sessions.
-- Throwaway proof for plan §5.3 bullet 3. Not production schema.

CREATE EXTENSION IF NOT EXISTS btree_gist;

DROP TABLE IF EXISTS work_session CASCADE;

CREATE TABLE work_session (
    work_session_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id         bigint NOT NULL,
    leaf_id         bigint NOT NULL,
    session_range   tstzrange NOT NULL,
    CHECK (lower(session_range) IS NOT NULL),
    -- same-user/same-leaf non-overlap, including open (unbounded-upper)
    -- sessions
    EXCLUDE USING gist (
        user_id WITH =,
        leaf_id WITH =,
        session_range WITH &&
    )
);

-- partial unique index: at most one *unfinished* (unbounded-upper)
-- session per user/leaf — belt-and-braces alongside the exclusion
-- constraint, matching plan §6.3.
CREATE UNIQUE INDEX idx_work_session_one_open_per_user_leaf
    ON work_session (user_id, leaf_id)
    WHERE upper_inf(session_range);
