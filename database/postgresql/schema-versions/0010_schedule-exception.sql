-- Schema version 0010 (PostgreSQL): additive/subtractive schedule
-- exceptions, optional additive-exception rates, and non-overlap of
-- explicitly priced additive exceptions. See impl plan §6.2 item 10,
-- spec §8.3, spec_claude §8.3/Appendix A.
--
-- span is modelled as started_at/finished_at instants rather than a single
-- tstzrange column, matching this schema's existing convention for other
-- historical/instant-bounded rows (job_node, work_session); "must have a
-- non-empty range" (spec §8.3) is the same finished_at > started_at check
-- used elsewhere, which also rules out an empty range.

CREATE TABLE schedule_exception_effect
(
    id   smallint PRIMARY KEY,
    name text NOT NULL UNIQUE
);

-- Spec §8.3: the two schedule-exception effects.
INSERT INTO schedule_exception_effect (id, name)
VALUES (1, 'AddWorkingTime'),
       (2, 'RemoveWorkingTime');

CREATE TABLE user_schedule_exception
(
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id       bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    started_at    timestamptz NOT NULL,
    finished_at   timestamptz NOT NULL,
    -- Stored projection of started_at/finished_at as a single range value,
    -- mirroring work_session.session_range (schema version 0007). Both
    -- bounds are NOT NULL here (unlike the effective-dated tables), so this
    -- is always a finite range. started_at/finished_at remain the canonical
    -- EF/domain-mapped columns; this is a PostgreSQL-only projection.
    exception_range tstzrange GENERATED ALWAYS AS (
        tstzrange(started_at, finished_at, '[)')
    ) STORED,
    effect_id     smallint NOT NULL REFERENCES schedule_exception_effect (id) ON DELETE RESTRICT,
    -- Spec §8.3: only AddWorkingTime may carry an explicit hourly
    -- rate_override; a RemoveWorkingTime exception removes availability, so
    -- pricing it is meaningless. effect_id 1 is the AddWorkingTime row
    -- seeded immediately above.
    rate_override numeric(19, 6),
    reason        text NOT NULL,
    created_by    bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    changed_at    timestamptz NOT NULL DEFAULT now(),
    row_version   bigint NOT NULL DEFAULT 1,
    CONSTRAINT user_schedule_exception_finished_after_started CHECK (finished_at > started_at),
    CONSTRAINT user_schedule_exception_reason_not_blank CHECK (btrim(reason) <> ''),
    CONSTRAINT user_schedule_exception_rate_override_non_negative CHECK (rate_override IS NULL OR rate_override >= 0),
    CONSTRAINT user_schedule_exception_rate_override_only_on_additive CHECK (rate_override IS NULL OR effect_id = 1),
    -- Spec §8.3: "For one user, two explicitly priced additive exceptions
    -- shall not overlap ... adjacent priced exceptions are valid. Unpriced
    -- additive exceptions may overlap and are normalised to their union."
    -- A partial exclusion constraint, scoped to priced (rate_override IS
    -- NOT NULL) additive (effect_id 1) rows only -- unpriced additive
    -- exceptions and all subtractive exceptions are deliberately
    -- unconstrained here. btree_gist (enabled in schema version 0007)
    -- supplies the "=" GiST strategy for the user_id equality term.
    CONSTRAINT user_schedule_exception_no_overlap_priced_additive
        EXCLUDE USING gist (
            user_id WITH =,
            exception_range WITH &&
        ) WHERE (effect_id = 1 AND rate_override IS NOT NULL)
);

CREATE INDEX user_schedule_exception_user_id_started_at_idx ON user_schedule_exception (user_id, started_at);
