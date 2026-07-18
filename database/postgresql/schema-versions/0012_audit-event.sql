-- Schema version 0012 (PostgreSQL): append-only audit_event storage and
-- access restrictions. See impl plan §6.2 item 12, spec §16, ADR 0001,
-- ADR 0003.
--
-- Structured columns for actor, operation, entity, correlation, and
-- timestamp per spec §12.4, with jsonb before/after payloads only for the
-- genuinely variable-shaped row-content snapshot (ADR 0003: "an audit
-- record capturing the full before and after row content, not just the
-- changed fields"). entity_id is deliberately not a foreign key: one
-- audit_event row can describe a change to any of several different
-- entity tables (job_node, leaf_work, work_session, schedules, rates,
-- ...), so there is no single referential target.
--
-- "Audit records shall be append-only to normal application roles" (spec
-- §16) is enforced here as an unconditional trigger, not merely a role
-- grant: role-based least-privilege database accounts (§6.1's "separate
-- owner, schema-deployer, application, read-only/reporting, and
-- emergency-reset roles") are provisioned as deployment-tool
-- infrastructure, not a per-schema-version script, and are additional
-- defense-in-depth layered on top of this trigger, not a replacement for
-- it -- a trigger holds even for a connection that has UPDATE/DELETE
-- privilege on the table.

CREATE TABLE audit_event
(
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at   timestamptz NOT NULL DEFAULT now(),
    actor_user_id bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    operation     text NOT NULL,
    entity_type   text NOT NULL,
    entity_id     bigint NOT NULL,
    correlation_id uuid NOT NULL,
    reason        text,
    before_data   jsonb,
    after_data    jsonb,
    CONSTRAINT audit_event_operation_not_blank CHECK (btrim(operation) <> ''),
    CONSTRAINT audit_event_entity_type_not_blank CHECK (btrim(entity_type) <> '')
);

CREATE INDEX audit_event_entity_type_entity_id_idx ON audit_event (entity_type, entity_id);
CREATE INDEX audit_event_correlation_id_idx ON audit_event (correlation_id);
CREATE INDEX audit_event_actor_user_id_occurred_at_idx ON audit_event (actor_user_id, occurred_at);

CREATE FUNCTION reject_audit_event_update() RETURNS trigger AS
$$
BEGIN
    RAISE EXCEPTION 'audit_event rows are append-only and cannot be updated';
END;
$$ LANGUAGE plpgsql;

CREATE FUNCTION reject_audit_event_delete() RETURNS trigger AS
$$
BEGIN
    RAISE EXCEPTION 'audit_event rows are append-only and cannot be deleted';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER audit_event_no_update
    BEFORE UPDATE
    ON audit_event
    FOR EACH ROW
EXECUTE FUNCTION reject_audit_event_update();

CREATE TRIGGER audit_event_no_delete
    BEFORE DELETE
    ON audit_event
    FOR EACH ROW
EXECUTE FUNCTION reject_audit_event_delete();
