-- Schema version 0004 (PostgreSQL): job_node (the hierarchy table), the
-- seeded priority reference table, and the permanent-root guard. See impl
-- plan §6.2 item 4, spec §4.1/§4.6/§11, spec_claude §3.1, ADR 0009,
-- ADR 0015, ADR 0021.
--
-- Hierarchy acyclicity/move validation (plan §6.2 item 5) and leaf/branch
-- exclusivity via leaf_work (item 6) are out of scope here -- this script
-- only establishes job_node's own shape, ownership, versioning, archival,
-- and the root's undeletable/un-re-parentable guard.

CREATE TABLE priority
(
    id   smallint PRIMARY KEY,
    name text NOT NULL UNIQUE
);

-- ADR 0021: the four priority levels, in escalating order.
INSERT INTO priority (id, name)
VALUES (1, 'Low'),
       (2, 'Medium'),
       (3, 'High'),
       (4, 'Urgent');

CREATE TABLE job_node
(
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    parent_id               bigint REFERENCES job_node (id) ON DELETE RESTRICT,
    description             text NOT NULL,
    write_up                text,
    posted_by_user_id       bigint NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    owner_user_id           bigint REFERENCES app_user (id) ON DELETE RESTRICT,
    expected_duration_hours numeric(18, 2),
    expected_cost           numeric(19, 6),
    needed_start            timestamptz,
    needed_finish           timestamptz,
    priority_id             smallint NOT NULL REFERENCES priority (id) ON DELETE RESTRICT,
    posted_at               timestamptz NOT NULL DEFAULT now(),
    archived_at             timestamptz,
    row_version             bigint NOT NULL DEFAULT 1,
    CONSTRAINT job_node_description_not_blank CHECK (btrim(description) <> ''),
    CONSTRAINT job_node_not_own_parent CHECK (parent_id IS NULL OR parent_id <> id),
    CONSTRAINT job_node_expected_duration_non_negative CHECK (expected_duration_hours IS NULL OR expected_duration_hours >= 0),
    CONSTRAINT job_node_expected_cost_non_negative CHECK (expected_cost IS NULL OR expected_cost >= 0),
    CONSTRAINT job_node_needed_finish_after_start CHECK (
        needed_start IS NULL OR needed_finish IS NULL OR needed_finish > needed_start),
    -- Ownership model §2.1: owner_user_id is nullable ("unassigned pool") for every node except
    -- the permanent root, whose owner is set at bootstrap and must always be non-null.
    CONSTRAINT job_node_root_owner_not_null CHECK (parent_id IS NOT NULL OR owner_user_id IS NOT NULL)
);

CREATE INDEX job_node_parent_id_idx ON job_node (parent_id);
CREATE INDEX job_node_owner_user_id_archived_at_idx ON job_node (owner_user_id, archived_at);

-- Spec §4.2/§12.4: "at most one root" is unconditional, at every instant,
-- regardless of bootstrap/initialisation state.
CREATE UNIQUE INDEX job_node_single_root_idx ON job_node ((parent_id IS NULL)) WHERE parent_id IS NULL;

-- ADR 0015: the permanent root, once armed by the initialised_marker row,
-- can never be deleted or re-parented. Ordinary updates to the root's other
-- columns remain allowed -- only DELETE and a parent_id change are blocked.
-- Separate functions for UPDATE/DELETE: a DELETE trigger has no NEW record,
-- so a shared function referencing NEW would fail at runtime on delete.
CREATE FUNCTION job_node_root_guard_on_update() RETURNS trigger AS
$$
BEGIN
    IF OLD.parent_id IS NOT NULL OR NOT EXISTS (SELECT 1 FROM initialised_marker) THEN
        RETURN NEW;
    END IF;

    IF NEW.parent_id IS DISTINCT FROM OLD.parent_id THEN
        RAISE EXCEPTION 'the permanent root cannot be re-parented (ADR 0015)';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE FUNCTION job_node_root_guard_on_delete() RETURNS trigger AS
$$
BEGIN
    IF OLD.parent_id IS NOT NULL OR NOT EXISTS (SELECT 1 FROM initialised_marker) THEN
        RETURN OLD;
    END IF;

    RAISE EXCEPTION 'the permanent root is undeletable (ADR 0015)';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER job_node_root_guard_on_update
    BEFORE UPDATE
    ON job_node
    FOR EACH ROW
EXECUTE FUNCTION job_node_root_guard_on_update();

CREATE TRIGGER job_node_root_guard_on_delete
    BEFORE DELETE
    ON job_node
    FOR EACH ROW
EXECUTE FUNCTION job_node_root_guard_on_delete();

-- The node an employee lands on after login instead of the tree root, purely a navigation
-- convenience with no ownership/authorization weight. Nullable (NULL means "root"); ON DELETE
-- SET NULL rather than RESTRICT because deleting a job node should never be blocked by someone's
-- unrelated landing preference. Must reference a Root or Branch node, never a Leaf -- enforced at
-- write time by the command, not as a standing constraint here, since a node's structural kind is
-- derived from its children rather than stored (ADR 0035) and can legitimately drift after the
-- preference is set.
ALTER TABLE app_user
    ADD COLUMN home_node_id bigint REFERENCES job_node (id) ON DELETE SET NULL;
