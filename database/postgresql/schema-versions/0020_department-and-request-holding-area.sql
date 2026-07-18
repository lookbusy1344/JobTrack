-- Schema version 0020 (PostgreSQL): department, app_user_department,
-- request_holding_area, job_request, and job_request_note -- the requester
-- intake schema (ADR 0033/0034,
-- docs/plans/2026-07-11-client-requester-intake-plan.md §4/§9 Stage 2/9). A
-- holding area is a configured job_node parent that accepts
-- requester-created children; job_request anchors requester
-- ownership/visibility to a specific job_node independently of
-- job_node.owner_user_id (technical ownership, unaffected) and
-- independently of later moves or decomposition. job_request.acknowledged_at
-- and job_request_note are ADR 0034 additions, edited into this pre-release
-- script in place per ADR 0011's not-yet-deployed exception.

CREATE TABLE department
(
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text    NOT NULL,
    is_active   boolean NOT NULL DEFAULT true,
    row_version bigint  NOT NULL DEFAULT 1,
    CONSTRAINT department_name_not_blank CHECK (btrim(name) <> '')
);

-- Uniqueness applies only among active departments: a retired department's
-- name is free to be reused by a new one.
CREATE UNIQUE INDEX department_name_active_idx ON department (name) WHERE is_active;

CREATE TABLE app_user_department
(
    app_user_id   bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    department_id bigint NOT NULL REFERENCES department (id) ON DELETE RESTRICT,
    is_primary    boolean,
    PRIMARY KEY (app_user_id, department_id)
);

CREATE INDEX app_user_department_department_id_idx ON app_user_department (department_id);

-- At most one primary department per user.
CREATE UNIQUE INDEX app_user_department_primary_idx ON app_user_department (app_user_id) WHERE is_primary;

CREATE TABLE request_holding_area
(
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_node_id           bigint   NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    department_id         bigint REFERENCES department (id) ON DELETE RESTRICT,
    name                  text     NOT NULL,
    default_priority_id   smallint NOT NULL REFERENCES priority (id) ON DELETE RESTRICT,
    default_owner_user_id bigint REFERENCES app_user (id) ON DELETE RESTRICT,
    is_active             boolean  NOT NULL DEFAULT true,
    row_version           bigint   NOT NULL DEFAULT 1,
    CONSTRAINT request_holding_area_name_not_blank CHECK (btrim(name) <> '')
);

CREATE INDEX request_holding_area_job_node_id_idx ON request_holding_area (job_node_id);
CREATE INDEX request_holding_area_department_id_idx ON request_holding_area (department_id);

CREATE TABLE job_request
(
    job_node_id            bigint PRIMARY KEY REFERENCES job_node (id) ON DELETE RESTRICT,
    requester_user_id      bigint      NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    holding_area_id        bigint      NOT NULL REFERENCES request_holding_area (id) ON DELETE RESTRICT,
    requester_reference    text,
    submitted_at           timestamptz NOT NULL DEFAULT now(),
    closed_to_requester_at timestamptz,
    acknowledged_at        timestamptz,
    acknowledged_by_user_id bigint REFERENCES app_user (id) ON DELETE RESTRICT,
    row_version            bigint      NOT NULL DEFAULT 1,
    CONSTRAINT job_request_acknowledgment_pair CHECK ((acknowledged_at IS NULL) = (acknowledged_by_user_id IS NULL))
);

CREATE INDEX job_request_requester_user_id_idx ON job_request (requester_user_id);
CREATE INDEX job_request_holding_area_id_idx ON job_request (holding_area_id);

-- A request anchor cannot point at the permanent root: the root has no
-- parent, so "anchor's parent_id IS NOT NULL" is exactly "anchor is not the
-- root". Not deferred: job_request.job_node_id's FK already requires the
-- referenced job_node row to exist before this insert, so there is no
-- multi-step ordering concern analogous to leaf/branch exclusivity.
CREATE FUNCTION check_job_request_anchor_is_not_root() RETURNS trigger AS
$$
BEGIN
    IF (SELECT parent_id FROM job_node WHERE id = NEW.job_node_id) IS NULL THEN
        RAISE EXCEPTION 'job_request cannot anchor to the permanent root (job_node %)', NEW.job_node_id;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER job_request_anchor_is_not_root
    AFTER INSERT ON job_request
    FOR EACH ROW
EXECUTE FUNCTION check_job_request_anchor_is_not_root();

CREATE FUNCTION reject_job_request_reacknowledge() RETURNS trigger AS
$$
BEGIN
    RAISE EXCEPTION 'job_request acknowledgment is immutable once set';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER job_request_no_reacknowledge
    BEFORE UPDATE OF acknowledged_at, acknowledged_by_user_id ON job_request
    FOR EACH ROW
    WHEN (OLD.acknowledged_at IS NOT NULL
        AND (OLD.acknowledged_at IS DISTINCT FROM NEW.acknowledged_at
            OR OLD.acknowledged_by_user_id IS DISTINCT FROM NEW.acknowledged_by_user_id))
EXECUTE FUNCTION reject_job_request_reacknowledge();

-- job_request_note (ADR 0034): an append-only notes/comments thread rooted
-- at the request's anchor job_node.id, written by either staff or the
-- requester. No row_version -- like audit_event (schema version 0012),
-- notes are immutable once written, enforced by the same
-- reject-update/reject-delete trigger pair, not merely left unmodified by
-- convention.
CREATE TABLE job_request_note
(
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_node_id             bigint      NOT NULL REFERENCES job_request (job_node_id) ON DELETE RESTRICT,
    author_user_id          bigint      NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    content                 text        NOT NULL,
    is_visible_to_requester boolean     NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT job_request_note_content_not_blank CHECK (btrim(content) <> '')
);

CREATE INDEX job_request_note_job_node_id_idx ON job_request_note (job_node_id);

CREATE FUNCTION reject_job_request_note_update() RETURNS trigger AS
$$
BEGIN
    RAISE EXCEPTION 'job_request_note rows are append-only and cannot be updated';
END;
$$ LANGUAGE plpgsql;

CREATE FUNCTION reject_job_request_note_delete() RETURNS trigger AS
$$
BEGIN
    RAISE EXCEPTION 'job_request_note rows are append-only and cannot be deleted';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER job_request_note_no_update
    BEFORE UPDATE
    ON job_request_note
    FOR EACH ROW
EXECUTE FUNCTION reject_job_request_note_update();

CREATE TRIGGER job_request_note_no_delete
    BEFORE DELETE
    ON job_request_note
    FOR EACH ROW
EXECUTE FUNCTION reject_job_request_note_delete();
