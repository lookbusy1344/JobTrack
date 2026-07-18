-- Schema version 0014 (SQLite): department, app_user_department,
-- request_holding_area, job_request, and job_request_note -- the requester
-- intake schema (ADR 0033/0034,
-- docs/plans/2026-07-11-client-requester-intake-plan.md §4/§9 Stage 2/9).
-- Mirrors PostgreSQL schema version 0020. Instants are stored as UTC ticks
-- (ADR 0007), matching every other Instant column in this schema.

CREATE TABLE department
(
    id          INTEGER PRIMARY KEY,
    name        TEXT    NOT NULL,
    is_active   INTEGER NOT NULL DEFAULT 1,
    row_version INTEGER NOT NULL DEFAULT 1,
    CHECK (trim(name) <> ''),
    CHECK (is_active IN (0, 1))
) STRICT;

-- Uniqueness applies only among active departments: a retired department's
-- name is free to be reused by a new one.
CREATE UNIQUE INDEX department_name_active_idx ON department (name) WHERE is_active = 1;

CREATE TABLE app_user_department
(
    app_user_id   INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    department_id INTEGER NOT NULL REFERENCES department (id) ON DELETE RESTRICT,
    is_primary    INTEGER,
    PRIMARY KEY (app_user_id, department_id),
    CHECK (is_primary IS NULL OR is_primary IN (0, 1))
) STRICT, WITHOUT ROWID;

CREATE INDEX app_user_department_department_id_idx ON app_user_department (department_id);

-- At most one primary department per user.
CREATE UNIQUE INDEX app_user_department_primary_idx ON app_user_department (app_user_id) WHERE is_primary = 1;

CREATE TABLE request_holding_area
(
    id                    INTEGER PRIMARY KEY,
    job_node_id           INTEGER NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    department_id         INTEGER REFERENCES department (id) ON DELETE RESTRICT,
    name                  TEXT    NOT NULL,
    default_priority_id   INTEGER NOT NULL REFERENCES priority (id) ON DELETE RESTRICT,
    default_owner_user_id INTEGER REFERENCES app_user (id) ON DELETE RESTRICT,
    is_active             INTEGER NOT NULL DEFAULT 1,
    row_version           INTEGER NOT NULL DEFAULT 1,
    CHECK (trim(name) <> ''),
    CHECK (is_active IN (0, 1))
) STRICT;

CREATE INDEX request_holding_area_job_node_id_idx ON request_holding_area (job_node_id);
CREATE INDEX request_holding_area_department_id_idx ON request_holding_area (department_id);

CREATE TABLE job_request
(
    job_node_id            INTEGER PRIMARY KEY REFERENCES job_node (id) ON DELETE RESTRICT,
    requester_user_id      INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    holding_area_id        INTEGER NOT NULL REFERENCES request_holding_area (id) ON DELETE RESTRICT,
    requester_reference    TEXT,
    submitted_at           INTEGER NOT NULL,
    closed_to_requester_at INTEGER,
    acknowledged_at        INTEGER,
    acknowledged_by_user_id INTEGER REFERENCES app_user (id) ON DELETE RESTRICT,
    row_version            INTEGER NOT NULL DEFAULT 1,
    CHECK ((acknowledged_at IS NULL) = (acknowledged_by_user_id IS NULL))
) STRICT, WITHOUT ROWID;

CREATE INDEX job_request_requester_user_id_idx ON job_request (requester_user_id);
CREATE INDEX job_request_holding_area_id_idx ON job_request (holding_area_id);

-- A request anchor cannot point at the permanent root: the root has no
-- parent, so "anchor's parent_id IS NOT NULL" is exactly "anchor is not the
-- root".
CREATE TRIGGER job_request_anchor_is_not_root
    AFTER INSERT
    ON job_request
BEGIN
    SELECT RAISE(ABORT, 'job_request cannot anchor to the permanent root')
    WHERE (SELECT parent_id FROM job_node WHERE id = NEW.job_node_id) IS NULL;
END;

CREATE TRIGGER job_request_no_reacknowledge
    BEFORE UPDATE OF acknowledged_at, acknowledged_by_user_id
    ON job_request
    FOR EACH ROW
    WHEN OLD.acknowledged_at IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'job_request acknowledgment is immutable once set');
END;

-- job_request_note (ADR 0034): an append-only notes/comments thread rooted
-- at the request's anchor job_node.id, written by either staff or the
-- requester. No row_version -- like audit_event (schema version 0012),
-- notes are immutable once written, enforced by the same
-- reject-update/reject-delete trigger pair.
CREATE TABLE job_request_note
(
    id                      INTEGER PRIMARY KEY,
    job_node_id             INTEGER NOT NULL REFERENCES job_request (job_node_id) ON DELETE RESTRICT,
    author_user_id          INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    content                 TEXT    NOT NULL,
    is_visible_to_requester INTEGER NOT NULL,
    created_at              INTEGER NOT NULL,
    CHECK (trim(content) <> ''),
    CHECK (is_visible_to_requester IN (0, 1))
) STRICT;

CREATE INDEX job_request_note_job_node_id_idx ON job_request_note (job_node_id);

CREATE TRIGGER job_request_note_no_update
    BEFORE UPDATE
    ON job_request_note
BEGIN
    SELECT RAISE(ABORT, 'job_request_note rows are append-only and cannot be updated');
END;

CREATE TRIGGER job_request_note_no_delete
    BEFORE DELETE
    ON job_request_note
BEGIN
    SELECT RAISE(ABORT, 'job_request_note rows are append-only and cannot be deleted');
END;
