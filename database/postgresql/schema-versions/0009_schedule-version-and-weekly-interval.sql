-- Schema version 0009 (PostgreSQL): effective-dated schedule versions and
-- weekly intervals. See impl plan §6.2 item 9, spec §8.1/§8.2,
-- spec_claude §8.1/§8.2, ADR 0008, ADR 0016.
--
-- Schedule exceptions (plan §6.2 item 10) are out of scope here.
--
-- effective_start/effective_end are civil dates, not instants -- ADR 0007's
-- tick encoding governs Instant-typed values (session/audit timestamps);
-- a bare calendar date has no time-of-day component and no DST-mapping
-- ambiguity of its own, so it uses PostgreSQL's native `date` here.
-- start_time/end_time on the weekly-interval side are civil times of day
-- (Noda Time `LocalTime`, ADR 0016); DST resolution when a weekly interval
-- is expanded against a specific calendar date is a domain-layer concern
-- (ADR 0008), not a schema-layer one.
--
-- Overlapping/adjacent weekly intervals *within* one schedule version
-- "should" (not "shall") be normalised to their union at calculation or
-- storage time (spec §8.2) -- a soft requirement left to the application
-- layer, not enforced as a hard database constraint here. The one "shall"
-- from spec §8.1 enforced here is that one user's schedule-version
-- effective ranges must not overlap.

CREATE TABLE user_schedule_version
(
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id         bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    effective_start date NOT NULL,
    effective_end   date,
    -- Stored projection of effective_start/effective_end as a single range
    -- value, mirroring work_session.session_range (schema version 0007).
    -- effective_start/effective_end remain the canonical EF/domain-mapped
    -- columns; this is a PostgreSQL-only query/constraint projection.
    effective_range daterange GENERATED ALWAYS AS (
        daterange(effective_start, COALESCE(effective_end, 'infinity'::date), '[)')
    ) STORED,
    iana_time_zone  text NOT NULL,
    changed_at      timestamptz NOT NULL DEFAULT now(),
    row_version     bigint NOT NULL DEFAULT 1,
    CONSTRAINT user_schedule_version_end_after_start CHECK (effective_end IS NULL OR effective_end > effective_start),
    -- Spec §8.1: effective ranges for schedule versions belonging to the
    -- same user shall not overlap. btree_gist (enabled in schema version
    -- 0007) supplies the "=" GiST strategy for the user_id equality term.
    CONSTRAINT user_schedule_version_no_overlap_per_user
        EXCLUDE USING gist (
            user_id WITH =,
            effective_range WITH &&
        )
);

CREATE INDEX user_schedule_version_user_id_idx ON user_schedule_version (user_id);

CREATE TABLE user_schedule_interval
(
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    schedule_version_id bigint NOT NULL REFERENCES user_schedule_version (id) ON DELETE CASCADE,
    -- ISO 8601 weekday numbering (Monday = 1 .. Sunday = 7), matching Noda
    -- Time's IsoDayOfWeek -- not a seeded reference table, since this is a
    -- fixed, closed 7-value domain intrinsic to the calendar rather than an
    -- application-defined lookup.
    day_of_week         smallint NOT NULL CHECK (day_of_week BETWEEN 1 AND 7),
    start_time          time NOT NULL,
    end_time            time NOT NULL,
    -- Spec §8.2: an interval may cross midnight. Rather than inferring
    -- "crosses midnight" from end_time <= start_time (ambiguous for a
    -- same-day interval mistakenly entered backwards), the author's intent
    -- is recorded explicitly; a crossing interval's actual local-date
    -- segmentation is normalised at calculation time (ADR 0008 territory),
    -- not stored pre-split here.
    crosses_midnight    boolean NOT NULL DEFAULT false,
    CONSTRAINT user_schedule_interval_non_empty CHECK (crosses_midnight OR end_time > start_time)
);

CREATE INDEX user_schedule_interval_schedule_version_id_idx ON user_schedule_interval (schedule_version_id);
