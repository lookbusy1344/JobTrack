-- Schema version 0008 (PostgreSQL): job_prerequisite, DAG enforcement, and
-- hierarchy-edge exclusion, revalidated on move. See impl plan §6.2 item 8,
-- spec §6, ADR 0012, spike 02-prerequisite-cycle.sql.
--
-- Direction: from_id is the required job, to_id is the dependent job
-- (spec §6: "RequiredJob -> DependentJob").
--
-- Readiness/eligibility queries (whether a leaf's own and inherited
-- prerequisites are satisfied) require achievement derivation, which is
-- schema slice 13's canonical query set -- out of scope here. This slice
-- establishes only the prerequisite graph's own structural invariants
-- (spec §6):
--   1. both endpoints reference existing job_node rows (FK);
--   2. a node cannot require itself (CHECK);
--   3. duplicate edges are prohibited (PK);
--   4. the prerequisite graph is acyclic (deferred constraint trigger);
--   5. an edge is prohibited when either endpoint is an ancestor or
--      descendant of the other (deferred constraint trigger, revalidated
--      whenever job_node.parent_id changes -- impl plan §6.2 item 5's
--      "a move can newly violate the ancestor/descendant prohibition even
--      while the tree stays acyclic").

CREATE TABLE job_prerequisite
(
    from_id bigint NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    to_id   bigint NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    PRIMARY KEY (from_id, to_id),
    CONSTRAINT job_prerequisite_not_self CHECK (from_id <> to_id)
);

CREATE INDEX job_prerequisite_to_id_idx ON job_prerequisite (to_id);

-- Reopening a successful prerequisite must not invalidate dependent work
-- which became active after the caller reviewed the dependent impact.
CREATE FUNCTION jobtrack_has_active_dependent_work(p_required_job_id bigint) RETURNS boolean AS
$$
    WITH RECURSIVE dependent_subtrees(id) AS (
        SELECT to_id FROM job_prerequisite WHERE from_id = p_required_job_id
        UNION
        SELECT jn.id FROM job_node jn JOIN dependent_subtrees ds ON jn.parent_id = ds.id
    )
    SELECT EXISTS (
        SELECT 1
        FROM work_session ws
        JOIN dependent_subtrees ds ON ds.id = ws.leaf_work_id
        WHERE ws.finished_at IS NULL
    );
$$ LANGUAGE sql STABLE;

-- Rule 4: acyclicity. Walks forward from the new edge's dependent node
-- (to_id); if it can already reach the required node (from_id), the new
-- edge would close a cycle.
CREATE FUNCTION check_job_prerequisite_no_cycle() RETURNS trigger AS
$$
DECLARE
    cycle_exists boolean;
BEGIN
    WITH RECURSIVE reachable(node_id) AS (
        SELECT to_id FROM job_prerequisite WHERE from_id = NEW.to_id
        UNION
        SELECT jp.to_id
        FROM job_prerequisite jp
        JOIN reachable r ON jp.from_id = r.node_id
    )
    SELECT EXISTS (SELECT 1 FROM reachable WHERE node_id = NEW.from_id) INTO cycle_exists;

    IF cycle_exists THEN
        RAISE EXCEPTION 'prerequisite edge % -> % would create a cycle', NEW.from_id, NEW.to_id;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER job_prerequisite_no_cycle
    AFTER INSERT ON job_prerequisite
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
EXECUTE FUNCTION check_job_prerequisite_no_cycle();

-- Rule 5 (edge side): reject a new edge whose endpoints are already
-- ancestor/descendant of each other in job_node.
CREATE FUNCTION check_job_prerequisite_not_hierarchy_edge() RETURNS trigger AS
$$
BEGIN
    IF EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_id FROM job_node WHERE id = NEW.to_id
            UNION ALL
            SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
        )
        SELECT 1 FROM ancestors WHERE id = NEW.from_id
    ) OR EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_id FROM job_node WHERE id = NEW.from_id
            UNION ALL
            SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
        )
        SELECT 1 FROM ancestors WHERE id = NEW.to_id
    ) THEN
        RAISE EXCEPTION 'prerequisite edge % -> % is prohibited: endpoints are ancestor/descendant in the job hierarchy',
            NEW.from_id, NEW.to_id;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER job_prerequisite_not_hierarchy_edge
    AFTER INSERT ON job_prerequisite
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
EXECUTE FUNCTION check_job_prerequisite_not_hierarchy_edge();

-- Rule 5 (move side): re-validate every existing prerequisite edge
-- whenever job_node.parent_id changes. Whole-graph re-check rather than
-- scoped to the moved subtree, because the moved node's new/lost
-- ancestors change which edges elsewhere in the tree remain valid too.
CREATE FUNCTION check_job_prerequisite_edges_after_move() RETURNS trigger AS
$$
DECLARE
    violation record;
BEGIN
    SELECT jp.from_id, jp.to_id INTO violation
    FROM job_prerequisite jp
    WHERE EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_id FROM job_node WHERE id = jp.to_id
            UNION ALL
            SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
        )
        SELECT 1 FROM ancestors WHERE id = jp.from_id
    ) OR EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_id FROM job_node WHERE id = jp.from_id
            UNION ALL
            SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
        )
        SELECT 1 FROM ancestors WHERE id = jp.to_id
    )
    LIMIT 1;

    IF FOUND THEN
        RAISE EXCEPTION 'moving job_node % under % would leave prerequisite edge % -> % as an ancestor/descendant edge',
            NEW.id, NEW.parent_id, violation.from_id, violation.to_id;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER job_prerequisite_edges_after_move
    AFTER UPDATE OF parent_id ON job_node
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    WHEN (NEW.parent_id IS NOT NULL)
EXECUTE FUNCTION check_job_prerequisite_edges_after_move();

-- ADR 0012: one fixed, well-known lock key (no entity id) serializing
-- every job_prerequisite edge insert -- proven necessary by
-- spikes/sql/02-prerequisite-cycle.sql (two individually-acyclic
-- concurrent inserts, e.g. A->B and B->A, can otherwise both commit and
-- jointly create a cycle, since each transaction's deferred check only
-- sees already-committed data, not the other's in-flight insert). Schema
-- slice 5's move_job_node acquires this same lock key as its final step,
-- closing the analogous cross-domain hazard between a move and a
-- concurrent edge insert -- see that function's header comment.
CREATE FUNCTION add_job_prerequisite(p_from_id bigint, p_to_id bigint) RETURNS void AS
$$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('jobtrack:prerequisite-graph-writes')::bigint);
    INSERT INTO job_prerequisite (from_id, to_id) VALUES (p_from_id, p_to_id);
END;
$$ LANGUAGE plpgsql;
