-- Schema version 0006 (PostgreSQL): leaf_work and leaf/branch/root
-- exclusivity. See impl plan §6.2 item 6, spec §4.2 rules 7-10, §4.3,
-- spec_claude §3.2/§3.3, ADR 0001.
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
-- 8, and 10 are enforced by the two deferred constraint triggers below.
--
-- Deferred per spec line 626/662: leaf/branch exclusivity should use
-- deferred constraint triggers so a valid multi-step structural operation
-- (spec §4.5 decomposition: add a child under a LeafWork node, then move
-- the LeafWork onto that new child) can complete within one transaction,
-- in any statement order, before the final check -- both triggers below
-- evaluate against final row state at commit, not intermediate state.

CREATE TABLE leaf_work
(
    job_node_id      bigint PRIMARY KEY REFERENCES job_node (id) ON DELETE RESTRICT,
    achievement_id   smallint NOT NULL DEFAULT 1 REFERENCES achievement_status (id) ON DELETE RESTRICT,
    partial_criteria text,
    full_criteria    text,
    changed_at       timestamptz NOT NULL DEFAULT now(),
    row_version      bigint NOT NULL DEFAULT 1
);

-- ADR 0012 lock-key construction, mirroring jobtrack_subtree_move_lock_key
-- (schema slice 5): a fixed namespace constant XORed with the contested
-- node id, keyed on whichever job_node's leaf/branch status is in
-- question -- the parent being attached to (child side) or the node
-- itself (LeafWork side).
CREATE FUNCTION jobtrack_leaf_branch_exclusivity_lock_key(p_node_id bigint) RETURNS bigint AS
$$
SELECT hashtext('jobtrack:leaf-branch-exclusivity')::bigint # p_node_id;
$$ LANGUAGE sql IMMUTABLE;

-- Rule 10 (the "child" side): a node cannot become a child of a node which
-- already holds LeafWork.
--
-- Concurrently attaching LeafWork to node N and inserting a child under N
-- are each individually valid under READ COMMITTED (neither sees the
-- other's uncommitted row) and can otherwise both commit, jointly
-- violating leaf/branch exclusivity -- the same cross-domain write-skew
-- shape proven for move_job_node/add_job_prerequisite in schema slice 5's
-- header comment. Proven here by
-- Concurrent_leaf_work_attachment_and_child_insertion_on_the_same_node_allow_exactly_one_to_succeed
-- in LeafWorkSchemaContractTestsBase, which failed intermittently (both
-- committing) before this lock was added. Both triggers below acquire the
-- advisory lock keyed on the contested node (NEW.parent_id here,
-- NEW.job_node_id in check_leaf_work_node_is_leaf) before reading the
-- other table, serializing the two paths for that node.
CREATE FUNCTION check_job_node_parent_has_no_leaf_work() RETURNS trigger AS
$$
BEGIN
    IF NEW.parent_id IS NULL THEN
        RETURN NULL;
    END IF;

    PERFORM pg_advisory_xact_lock(jobtrack_leaf_branch_exclusivity_lock_key(NEW.parent_id));

    IF EXISTS (SELECT 1 FROM leaf_work WHERE job_node_id = NEW.parent_id) THEN
        RAISE EXCEPTION 'job_node % cannot be a child of % because % holds LeafWork (leaf/branch exclusivity)',
            NEW.id, NEW.parent_id, NEW.parent_id;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER job_node_parent_has_no_leaf_work
    AFTER INSERT OR UPDATE OF parent_id ON job_node
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    WHEN (NEW.parent_id IS NOT NULL)
EXECUTE FUNCTION check_job_node_parent_has_no_leaf_work();

-- Rules 7 and 10 (the "LeafWork" side): LeafWork cannot attach to the root
-- or to a node which has children. See the header comment on
-- check_job_node_parent_has_no_leaf_work above for why this trigger
-- acquires the same lock key (on NEW.job_node_id) before its checks.
CREATE FUNCTION check_leaf_work_node_is_leaf() RETURNS trigger AS
$$
DECLARE
    v_parent_id bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(jobtrack_leaf_branch_exclusivity_lock_key(NEW.job_node_id));

    SELECT parent_id INTO v_parent_id FROM job_node WHERE id = NEW.job_node_id;

    IF v_parent_id IS NULL THEN
        RAISE EXCEPTION 'the root cannot hold LeafWork (job_node %)', NEW.job_node_id;
    END IF;

    IF EXISTS (SELECT 1 FROM job_node WHERE parent_id = NEW.job_node_id) THEN
        RAISE EXCEPTION 'job_node % has children and cannot hold LeafWork (leaf/branch exclusivity)', NEW.job_node_id;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER leaf_work_node_is_leaf
    AFTER INSERT OR UPDATE OF job_node_id ON leaf_work
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
EXECUTE FUNCTION check_leaf_work_node_is_leaf();
