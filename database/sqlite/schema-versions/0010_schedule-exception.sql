-- Schema version 0010 (SQLite): additive/subtractive schedule exceptions,
-- optional additive-exception rates, and non-overlap of explicitly priced
-- additive exceptions. See impl plan §6.2 item 10, spec §8.3,
-- spec_claude §8.3/Appendix A, ADR 0007, ADR 0009.
--
-- rate_override is a canonical fixed-point decimal string (SQLite has no
-- native fixed-precision numeric type; precision is application-enforced
-- per ADR 0009, matching job_node.expected_cost). SQLite has no partial
-- GiST exclusion constraints, so the priced-additive non-overlap rule uses
-- immediate triggers, mirroring 0009's schedule-version overlap triggers.

CREATE TABLE schedule_exception_effect
(
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
) STRICT;

-- Spec §8.3: the two schedule-exception effects.
INSERT INTO schedule_exception_effect (id, name)
VALUES (1, 'AddWorkingTime'),
       (2, 'RemoveWorkingTime');

CREATE TABLE user_schedule_exception
(
    id            INTEGER PRIMARY KEY,
    user_id       INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    started_at    INTEGER NOT NULL,
    finished_at   INTEGER NOT NULL,
    effect_id     INTEGER NOT NULL REFERENCES schedule_exception_effect (id) ON DELETE RESTRICT,
    -- Spec §8.3: only AddWorkingTime may carry an explicit hourly
    -- rate_override; a RemoveWorkingTime exception removes availability, so
    -- pricing it is meaningless. effect_id 1 is the AddWorkingTime row
    -- seeded immediately above.
    rate_override TEXT CHECK (rate_override IS NULL OR CAST(rate_override AS REAL) >= 0),
    reason        TEXT NOT NULL CHECK (trim(reason) <> ''),
    created_by    INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    changed_at    INTEGER NOT NULL,
    row_version   INTEGER NOT NULL DEFAULT 1,
    CHECK (finished_at > started_at),
    CHECK (rate_override IS NULL OR effect_id = 1)
) STRICT;

CREATE INDEX user_schedule_exception_user_id_started_at_idx ON user_schedule_exception (user_id, started_at);

CREATE TRIGGER user_schedule_exception_no_overlap_priced_additive_on_insert
    AFTER INSERT
    ON user_schedule_exception
    WHEN NEW.effect_id = 1 AND NEW.rate_override IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'overlapping priced additive schedule exception for this user')
    WHERE EXISTS (
        SELECT 1
        FROM user_schedule_exception se
        WHERE se.id <> NEW.id
          AND se.user_id = NEW.user_id
          AND se.effect_id = 1
          AND se.rate_override IS NOT NULL
          AND se.started_at < NEW.finished_at
          AND NEW.started_at < se.finished_at
    );
END;

CREATE TRIGGER user_schedule_exception_no_overlap_priced_additive_on_update
    AFTER UPDATE OF user_id, started_at, finished_at, effect_id, rate_override
    ON user_schedule_exception
    WHEN NEW.effect_id = 1 AND NEW.rate_override IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'overlapping priced additive schedule exception for this user')
    WHERE EXISTS (
        SELECT 1
        FROM user_schedule_exception se
        WHERE se.id <> NEW.id
          AND se.user_id = NEW.user_id
          AND se.effect_id = 1
          AND se.rate_override IS NOT NULL
          AND se.started_at < NEW.finished_at
          AND NEW.started_at < se.finished_at
    );
END;
