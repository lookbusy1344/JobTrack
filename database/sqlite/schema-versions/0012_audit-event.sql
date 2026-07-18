-- Schema version 0012 (SQLite): append-only audit_event storage and access
-- restrictions. See impl plan §6.2 item 12, spec §16, ADR 0001, ADR 0003,
-- ADR 0007.
--
-- correlation_id is a canonical 36-character text UUID (SQLite has no
-- native uuid type); before_data/after_data are JSON text (SQLite's json1
-- functions operate on TEXT). See the sibling PostgreSQL script's header
-- for the full column-shape rationale and the "trigger, not just a role
-- grant" append-only enforcement rationale.

CREATE TABLE audit_event
(
    id             INTEGER PRIMARY KEY,
    occurred_at    INTEGER NOT NULL,
    actor_user_id  INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    operation      TEXT NOT NULL CHECK (trim(operation) <> ''),
    entity_type    TEXT NOT NULL CHECK (trim(entity_type) <> ''),
    entity_id      INTEGER NOT NULL,
    correlation_id TEXT NOT NULL,
    reason         TEXT,
    before_data    TEXT,
    after_data     TEXT
) STRICT;

CREATE INDEX audit_event_entity_type_entity_id_idx ON audit_event (entity_type, entity_id);
CREATE INDEX audit_event_correlation_id_idx ON audit_event (correlation_id);
CREATE INDEX audit_event_actor_user_id_occurred_at_idx ON audit_event (actor_user_id, occurred_at);

CREATE TRIGGER audit_event_no_update
    BEFORE UPDATE
    ON audit_event
BEGIN
    SELECT RAISE(ABORT, 'audit_event rows are append-only and cannot be updated');
END;

CREATE TRIGGER audit_event_no_delete
    BEFORE DELETE
    ON audit_event
BEGIN
    SELECT RAISE(ABORT, 'audit_event rows are append-only and cannot be deleted');
END;
