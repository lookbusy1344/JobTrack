-- Schema version 0009 (SQLite): effective-dated schedule versions and
-- weekly intervals. See impl plan §6.2 item 9, spec §8.1/§8.2,
-- spec_claude §8.1/§8.2, ADR 0007, ADR 0008, ADR 0016.
--
-- Schedule exceptions (plan §6.2 item 10) are out of scope here.
--
-- effective_start/effective_end are civil dates, not instants -- ADR 0007's
-- INTEGER tick encoding governs Instant-typed values (session/audit
-- timestamps); a bare calendar date has no time-of-day component, so a
-- fixed-width ISO-8601 'YYYY-MM-DD' TEXT value is used instead: unlike a
-- full timestamp, it sorts and range-compares correctly as text and needs
-- no precision decisions. start_time/end_time on the weekly-interval side
-- are civil times of day (Noda Time `LocalTime`); they use the same
-- 100ns-tick unit as ADR 0007's instants, but counted since local
-- midnight (0..863999999999) rather than since the Unix epoch, since they
-- carry no date or zone of their own.
--
-- SQLite has no GiST exclusion constraints, so the one "shall" this slice
-- enforces (spec §8.1: one user's schedule-version effective ranges must
-- not overlap) uses immediate triggers, mirroring 0007's work_session
-- overlap triggers. Overlapping/adjacent weekly intervals *within* one
-- version are a "should"-level application-layer normalisation concern
-- (spec §8.2), not enforced here.

CREATE TABLE user_schedule_version
(
    id              INTEGER PRIMARY KEY,
    user_id         INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    effective_start TEXT NOT NULL,
    effective_end   TEXT,
    iana_time_zone  TEXT NOT NULL,
    changed_at      INTEGER NOT NULL,
    row_version     INTEGER NOT NULL DEFAULT 1,
    CHECK (effective_end IS NULL OR effective_end > effective_start)
) STRICT;

CREATE INDEX user_schedule_version_user_id_idx ON user_schedule_version (user_id);

CREATE TRIGGER user_schedule_version_no_overlap_per_user_on_insert
    AFTER INSERT
    ON user_schedule_version
BEGIN
    SELECT RAISE(ABORT, 'overlapping schedule-version effective range for this user')
    WHERE EXISTS (
        SELECT 1
        FROM user_schedule_version sv
        WHERE sv.id <> NEW.id
          AND sv.user_id = NEW.user_id
          AND (sv.effective_end IS NULL OR NEW.effective_start < sv.effective_end)
          AND (NEW.effective_end IS NULL OR sv.effective_start < NEW.effective_end)
    );
END;

CREATE TRIGGER user_schedule_version_no_overlap_per_user_on_update
    AFTER UPDATE OF user_id, effective_start, effective_end
    ON user_schedule_version
BEGIN
    SELECT RAISE(ABORT, 'overlapping schedule-version effective range for this user')
    WHERE EXISTS (
        SELECT 1
        FROM user_schedule_version sv
        WHERE sv.id <> NEW.id
          AND sv.user_id = NEW.user_id
          AND (sv.effective_end IS NULL OR NEW.effective_start < sv.effective_end)
          AND (NEW.effective_end IS NULL OR sv.effective_start < NEW.effective_end)
    );
END;

CREATE TABLE user_schedule_interval
(
    id                  INTEGER PRIMARY KEY,
    schedule_version_id INTEGER NOT NULL REFERENCES user_schedule_version (id) ON DELETE CASCADE,
    -- ISO 8601 weekday numbering (Monday = 1 .. Sunday = 7), matching Noda
    -- Time's IsoDayOfWeek -- not a seeded reference table, since this is a
    -- fixed, closed 7-value domain intrinsic to the calendar rather than an
    -- application-defined lookup.
    day_of_week         INTEGER NOT NULL CHECK (day_of_week BETWEEN 1 AND 7),
    start_time          INTEGER NOT NULL,
    end_time            INTEGER NOT NULL,
    -- Spec §8.2: an interval may cross midnight. Rather than inferring
    -- "crosses midnight" from end_time <= start_time (ambiguous for a
    -- same-day interval mistakenly entered backwards), the author's intent
    -- is recorded explicitly; a crossing interval's actual local-date
    -- segmentation is normalised at calculation time, not stored pre-split
    -- here.
    crosses_midnight    INTEGER NOT NULL DEFAULT 0 CHECK (crosses_midnight IN (0, 1)),
    CHECK (crosses_midnight = 1 OR end_time > start_time)
) STRICT;

CREATE INDEX user_schedule_interval_schedule_version_id_idx ON user_schedule_interval (schedule_version_id);
