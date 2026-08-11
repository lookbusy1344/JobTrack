-- Schema version 0011 (SQLite): effective-dated user rates and inherited
-- node overrides. See impl plan §6.2 item 11, spec §9.1/§9.2,
-- spec_claude §9.1/§9.2, ADR 0007, ADR 0009.
--
-- Rate precedence (spec §9.3) and the nearest-ancestor override search
-- (spec §9.2) are Application/Domain-layer query concerns, not schema-layer
-- ones. rate is a canonical fixed-point decimal string (ADR 0009, matching
-- job_node.expected_cost); effective_start/effective_end are instant ticks
-- (ADR 0007), unlike user_schedule_version's civil dates (0009).
--
-- SQLite has no GiST exclusion constraints, so both non-overlap rules use
-- immediate triggers, mirroring 0009's schedule-version and 0010's
-- schedule-exception overlap triggers.

CREATE TABLE user_cost_rate
(
    id              INTEGER PRIMARY KEY,
    user_id         INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    effective_start INTEGER NOT NULL,
    effective_end   INTEGER,
    rate            TEXT NOT NULL CHECK (CAST(rate AS REAL) >= 0),
    changed_at      INTEGER NOT NULL,
    row_version     INTEGER NOT NULL DEFAULT 1,
    CHECK (effective_end IS NULL OR effective_end > effective_start)
) STRICT;

CREATE INDEX user_cost_rate_user_id_idx ON user_cost_rate (user_id);

CREATE TRIGGER user_cost_rate_no_overlap_per_user_on_insert
    AFTER INSERT
    ON user_cost_rate
BEGIN
    SELECT RAISE(ABORT, 'overlapping user_cost_rate effective range for this user')
    WHERE EXISTS (
        SELECT 1
        FROM user_cost_rate ucr
        WHERE ucr.id <> NEW.id
          AND ucr.user_id = NEW.user_id
          AND (ucr.effective_end IS NULL OR NEW.effective_start < ucr.effective_end)
          AND (NEW.effective_end IS NULL OR ucr.effective_start < NEW.effective_end)
    );
END;

CREATE TRIGGER user_cost_rate_no_overlap_per_user_on_update
    AFTER UPDATE OF user_id, effective_start, effective_end
    ON user_cost_rate
BEGIN
    SELECT RAISE(ABORT, 'overlapping user_cost_rate effective range for this user')
    WHERE EXISTS (
        SELECT 1
        FROM user_cost_rate ucr
        WHERE ucr.id <> NEW.id
          AND ucr.user_id = NEW.user_id
          AND (ucr.effective_end IS NULL OR NEW.effective_start < ucr.effective_end)
          AND (NEW.effective_end IS NULL OR ucr.effective_start < NEW.effective_end)
    );
END;

CREATE TABLE node_rate_override
(
    id              INTEGER PRIMARY KEY,
    node_id         INTEGER NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    user_id         INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    effective_start INTEGER NOT NULL,
    effective_end   INTEGER,
    rate            TEXT NOT NULL CHECK (CAST(rate AS REAL) >= 0),
    changed_at      INTEGER NOT NULL,
    row_version     INTEGER NOT NULL DEFAULT 1,
    CHECK (effective_end IS NULL OR effective_end > effective_start)
) STRICT;

CREATE INDEX node_rate_override_node_id_user_id_idx ON node_rate_override (node_id, user_id);

CREATE TRIGGER node_rate_override_no_overlap_per_node_and_user_on_insert
    AFTER INSERT
    ON node_rate_override
BEGIN
    SELECT RAISE(ABORT, 'overlapping node_rate_override effective range for this node and user')
    WHERE EXISTS (
        SELECT 1
        FROM node_rate_override nro
        WHERE nro.id <> NEW.id
          AND nro.node_id = NEW.node_id
          AND nro.user_id = NEW.user_id
          AND (nro.effective_end IS NULL OR NEW.effective_start < nro.effective_end)
          AND (NEW.effective_end IS NULL OR nro.effective_start < NEW.effective_end)
    );
END;

CREATE TRIGGER node_rate_override_no_overlap_per_node_and_user_on_update
    AFTER UPDATE OF node_id, user_id, effective_start, effective_end
    ON node_rate_override
BEGIN
    SELECT RAISE(ABORT, 'overlapping node_rate_override effective range for this node and user')
    WHERE EXISTS (
        SELECT 1
        FROM node_rate_override nro
        WHERE nro.id <> NEW.id
          AND nro.node_id = NEW.node_id
          AND nro.user_id = NEW.user_id
          AND (nro.effective_end IS NULL OR NEW.effective_start < nro.effective_end)
          AND (NEW.effective_end IS NULL OR nro.effective_start < NEW.effective_end)
    );
END;
