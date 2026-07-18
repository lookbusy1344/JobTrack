-- Schema version 0006 (SQLite): leaf_work and leaf/branch/root exclusivity.
-- See impl plan §6.2 item 6, spec §4.2 rules 7-10, §4.3, spec_claude
-- §3.2/§3.3, ADR 0001, ADR 0007.
--
-- work_session (plan §6.2 item 7) is out of scope here -- this script only
-- establishes leaf_work's own shape and the two structural exclusivity
-- invariants that do not depend on it:
--   7. the root cannot hold LeafWork;
--   8. a branch (>=1 children) has no LeafWork;
--   9. a leaf has no children and zero-or-one LeafWork;
--  10. a node can never have both a child and LeafWork.
-- Rule 9's "zero-or-one" half is the leaf_work.job_node_id primary key
-- (also its foreign key to job_node, giving the 1:1 relationship). Rules 7,
-- 8, and 10 are enforced by the four immediate triggers below.
--
-- SQLite has no deferred constraint triggers (spec line 652), and a single
-- CREATE TRIGGER cannot combine "INSERT" with "UPDATE OF column" the way
-- PostgreSQL's CREATE CONSTRAINT TRIGGER can, so each of the two invariants
-- below needs one INSERT trigger and one UPDATE-of-column trigger. Checks
-- are therefore immediate, evaluated against the state that exists right
-- after each statement, not deferred to commit -- unlike the PostgreSQL
-- constraint triggers in the sibling script. The full atomic decomposition
-- operation from spec §4.5 (which needs a leaf's LeafWork attached to a
-- not-yet-created child while other children are added to the original
-- node) is out of scope for this schema slice: it requires work_session
-- (plan §6.2 item 7) and is exercised at TC-DB-LEAF-002, not here.

CREATE TABLE leaf_work
(
    job_node_id      INTEGER PRIMARY KEY REFERENCES job_node (id) ON DELETE RESTRICT,
    achievement_id   INTEGER NOT NULL DEFAULT 1 REFERENCES achievement_status (id) ON DELETE RESTRICT,
    partial_criteria TEXT,
    full_criteria    TEXT,
    changed_at       INTEGER NOT NULL,
    row_version      INTEGER NOT NULL DEFAULT 1
) STRICT, WITHOUT ROWID;

-- Rule 10 (the "child" side): a node cannot become a child of a node which
-- already holds LeafWork.
CREATE TRIGGER job_node_parent_has_no_leaf_work_on_insert
    AFTER INSERT
    ON job_node
    WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'job_node cannot be a child of a node that holds LeafWork (leaf/branch exclusivity)')
    WHERE EXISTS (SELECT 1 FROM leaf_work WHERE job_node_id = NEW.parent_id);
END;

CREATE TRIGGER job_node_parent_has_no_leaf_work_on_update
    AFTER UPDATE OF parent_id
    ON job_node
    WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'job_node cannot be a child of a node that holds LeafWork (leaf/branch exclusivity)')
    WHERE EXISTS (SELECT 1 FROM leaf_work WHERE job_node_id = NEW.parent_id);
END;

-- Rules 7 and 10 (the "LeafWork" side): LeafWork cannot attach to the root
-- or to a node which has children.
CREATE TRIGGER leaf_work_node_is_leaf_on_insert
    AFTER INSERT
    ON leaf_work
BEGIN
    SELECT RAISE(ABORT, 'the root cannot hold LeafWork')
    WHERE (SELECT parent_id FROM job_node WHERE id = NEW.job_node_id) IS NULL;

    SELECT RAISE(ABORT, 'job_node has children and cannot hold LeafWork (leaf/branch exclusivity)')
    WHERE EXISTS (SELECT 1 FROM job_node WHERE parent_id = NEW.job_node_id);
END;

CREATE TRIGGER leaf_work_node_is_leaf_on_update
    AFTER UPDATE OF job_node_id
    ON leaf_work
BEGIN
    SELECT RAISE(ABORT, 'the root cannot hold LeafWork')
    WHERE (SELECT parent_id FROM job_node WHERE id = NEW.job_node_id) IS NULL;

    SELECT RAISE(ABORT, 'job_node has children and cannot hold LeafWork (leaf/branch exclusivity)')
    WHERE EXISTS (SELECT 1 FROM job_node WHERE parent_id = NEW.job_node_id);
END;
