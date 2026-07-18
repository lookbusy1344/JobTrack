-- Schema version 0004 (SQLite): job_node (the hierarchy table), the seeded
-- priority reference table, and the permanent-root guard. See impl plan
-- §6.2 item 4, spec §4.1/§4.6/§11, spec_claude §3.1, ADR 0007, ADR 0009,
-- ADR 0015, ADR 0021.
--
-- Hierarchy acyclicity/move validation (plan §6.2 item 5) and leaf/branch
-- exclusivity via leaf_work (item 6) are out of scope here -- this script
-- only establishes job_node's own shape, ownership, versioning, archival,
-- and the root's undeletable/un-re-parentable guard.
--
-- needed_start/needed_finish/posted_at/archived_at are signed 64-bit UTC
-- tick counts (100ns ticks since the Unix epoch), per ADR 0007. expected_cost
-- and expected_duration_hours are canonical fixed-point decimal strings
-- (SQLite has no native fixed-precision numeric type; precision is
-- application-enforced per ADR 0009).

CREATE TABLE priority
(
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
) STRICT;

INSERT INTO priority (id, name)
VALUES (1, 'Low'),
       (2, 'Medium'),
       (3, 'High'),
       (4, 'Urgent');

CREATE TABLE job_node
(
    id                      INTEGER PRIMARY KEY,
    parent_id               INTEGER REFERENCES job_node (id) ON DELETE RESTRICT,
    description             TEXT NOT NULL CHECK (trim(description) <> ''),
    write_up                TEXT,
    posted_by_user_id       INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    owner_user_id           INTEGER REFERENCES app_user (id) ON DELETE RESTRICT,
    expected_duration_hours TEXT CHECK (expected_duration_hours IS NULL OR CAST(expected_duration_hours AS REAL) >= 0),
    expected_cost           TEXT CHECK (expected_cost IS NULL OR CAST(expected_cost AS REAL) >= 0),
    needed_start            INTEGER,
    needed_finish           INTEGER,
    priority_id             INTEGER NOT NULL REFERENCES priority (id) ON DELETE RESTRICT,
    posted_at               INTEGER NOT NULL,
    archived_at             INTEGER,
    row_version             INTEGER NOT NULL DEFAULT 1,
    CHECK (parent_id IS NULL OR parent_id <> id),
    CHECK (needed_start IS NULL OR needed_finish IS NULL OR needed_finish > needed_start),
    -- Ownership model §2.1: owner_user_id is nullable ("unassigned pool") for every node except
    -- the permanent root, whose owner is set at bootstrap and must always be non-null.
    CHECK (parent_id IS NOT NULL OR owner_user_id IS NOT NULL)
) STRICT;

CREATE INDEX job_node_parent_id_idx ON job_node (parent_id);
CREATE INDEX job_node_owner_user_id_archived_at_idx ON job_node (owner_user_id, archived_at);

-- Spec §4.2/§12.4: "at most one root" is unconditional, at every instant,
-- regardless of bootstrap/initialisation state.
CREATE UNIQUE INDEX job_node_single_root_idx ON job_node ((parent_id IS NULL)) WHERE parent_id IS NULL;

-- ADR 0015: the permanent root, once armed by the initialised_marker row,
-- can never be deleted or re-parented. Ordinary updates to the root's other
-- columns remain allowed -- only DELETE and a parent_id change are blocked.
CREATE TRIGGER job_node_root_guard_on_update
    BEFORE UPDATE
    ON job_node
    WHEN OLD.parent_id IS NULL
        AND NEW.parent_id IS NOT NULL
        AND EXISTS (SELECT 1 FROM initialised_marker)
BEGIN
    SELECT RAISE(ABORT, 'the permanent root cannot be re-parented (ADR 0015)');
END;

CREATE TRIGGER job_node_root_guard_on_delete
    BEFORE DELETE
    ON job_node
    WHEN OLD.parent_id IS NULL
        AND EXISTS (SELECT 1 FROM initialised_marker)
BEGIN
    SELECT RAISE(ABORT, 'the permanent root is undeletable (ADR 0015)');
END;

-- The node an employee lands on after login instead of the tree root, purely a navigation
-- convenience with no ownership/authorization weight. Nullable (NULL means "root"); ON DELETE
-- SET NULL rather than RESTRICT because deleting a job node should never be blocked by someone's
-- unrelated landing preference. Must reference a Root or Branch node, never a Leaf -- enforced at
-- write time by the command, not as a standing constraint here, since a node's structural kind is
-- derived from its children rather than stored (ADR 0035) and can legitimately drift after the
-- preference is set.
ALTER TABLE app_user
    ADD COLUMN home_node_id INTEGER REFERENCES job_node (id) ON DELETE SET NULL;
