-- Schema version 0011 (PostgreSQL): effective-dated user rates and
-- inherited node overrides. See impl plan §6.2 item 11, spec §9.1/§9.2,
-- spec_claude §9.1/§9.2.
--
-- Rate precedence (spec §9.3) and the nearest-ancestor override search
-- (spec §9.2) are Application/Domain-layer query concerns, not schema-layer
-- ones -- this slice establishes only the two rate tables' own storage and
-- non-overlap invariants. app_user.default_hourly_rate (the lowest-
-- precedence source) already exists from schema version 0002.
--
-- Both tables' effective ranges are instant ranges (timestamptz), unlike
-- user_schedule_version's civil dates (0009) -- spec §9.1/§9.2 describe
-- rate effectiveness directly in terms of "effective at instant t", with
-- no user-time-zone interpretation step.

CREATE TABLE user_cost_rate
(
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id         bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    effective_start timestamptz NOT NULL,
    effective_end   timestamptz,
    -- Stored projection of effective_start/effective_end as a single range
    -- value, mirroring work_session.session_range (schema version 0007).
    -- effective_start/effective_end remain the canonical EF/domain-mapped
    -- columns; this is a PostgreSQL-only query/constraint projection, also
    -- used by resolve_rate/user_rate_boundaries (schema version 0015).
    effective_range tstzrange GENERATED ALWAYS AS (
        tstzrange(effective_start, COALESCE(effective_end, 'infinity'::timestamptz), '[)')
    ) STORED,
    rate            numeric(19, 6) NOT NULL,
    changed_at      timestamptz NOT NULL DEFAULT now(),
    row_version     bigint NOT NULL DEFAULT 1,
    CONSTRAINT user_cost_rate_end_after_start CHECK (effective_end IS NULL OR effective_end > effective_start),
    CONSTRAINT user_cost_rate_non_negative CHECK (rate >= 0),
    -- Spec §9.1: for a given user, effective ranges must not overlap;
    -- adjacent ranges are valid. btree_gist (enabled in schema version
    -- 0007) supplies the "=" GiST strategy for the user_id equality term.
    CONSTRAINT user_cost_rate_no_overlap_per_user
        EXCLUDE USING gist (
            user_id WITH =,
            effective_range WITH &&
        )
);

CREATE INDEX user_cost_rate_user_id_idx ON user_cost_rate (user_id);

CREATE TABLE node_rate_override
(
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    node_id         bigint NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    user_id         bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    effective_start timestamptz NOT NULL,
    effective_end   timestamptz,
    -- Stored projection of effective_start/effective_end as a single range
    -- value, mirroring work_session.session_range (schema version 0007).
    -- effective_start/effective_end remain the canonical EF/domain-mapped
    -- columns; this is a PostgreSQL-only query/constraint projection, also
    -- used by resolve_rate/user_rate_boundaries (schema version 0015).
    effective_range tstzrange GENERATED ALWAYS AS (
        tstzrange(effective_start, COALESCE(effective_end, 'infinity'::timestamptz), '[)')
    ) STORED,
    rate            numeric(19, 6) NOT NULL,
    changed_at      timestamptz NOT NULL DEFAULT now(),
    row_version     bigint NOT NULL DEFAULT 1,
    CONSTRAINT node_rate_override_end_after_start CHECK (effective_end IS NULL OR effective_end > effective_start),
    CONSTRAINT node_rate_override_non_negative CHECK (rate >= 0),
    -- Spec §9.2: overrides for the same node and user shall not have
    -- overlapping effective ranges; adjacent ranges are valid.
    CONSTRAINT node_rate_override_no_overlap_per_node_and_user
        EXCLUDE USING gist (
            node_id WITH =,
            user_id WITH =,
            effective_range WITH &&
        )
);

CREATE INDEX node_rate_override_node_id_user_id_idx ON node_rate_override (node_id, user_id);
