-- Schema version 0013 (PostgreSQL): canonical hierarchy traversal,
-- recursively derived achievement, and prerequisite readiness/diagnostic
-- queries. See impl plan §6.2 item 13, §6.5, spec §4/§5.2/§6, spec_claude
-- Appendix C.1/C.2.
--
-- This is the first of three sub-slices covering impl plan item 13's five
-- query families (hierarchy, achievement, eligibility, overlap-candidate,
-- cost-input); overlap-candidate discovery and the full cost-input sweep
-- (spec_claude Appendix C.4, needing the schedule/rate helpers from slices
-- 9-11) are out of scope here.
--
-- node_succeeded's boolean result is exactly spec §5.2's recursive
-- definition ("a branch succeeds iff every direct child succeeds") reduced
-- to a single set-based equivalent: by structural induction, a subtree
-- succeeds iff no childless node within it fails to hold Success
-- LeafWork. A childless node without LeafWork (an "empty" branch, or a
-- leaf lacking LeafWork) is exactly the failing case Appendix C.1's
-- explicit CASE handles ("a node with no leaf_work and no children never
-- succeeds") -- this form avoids per-row recursive function calls and the
-- bottom-up-by-depth aggregation the illustrative Appendix pseudocode
-- would otherwise need.
CREATE FUNCTION node_succeeded(p_node_id bigint) RETURNS boolean AS
$$
    WITH RECURSIVE subtree(id) AS (
        SELECT p_node_id
        UNION ALL
        SELECT jn.id FROM job_node jn JOIN subtree s ON jn.parent_id = s.id
    )
    SELECT NOT EXISTS (
        SELECT 1
        FROM subtree s
        WHERE NOT EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = s.id)
          AND NOT EXISTS (
              SELECT 1
              FROM leaf_work lw
              JOIN achievement_status a ON a.id = lw.achievement_id
              WHERE lw.job_node_id = s.id AND a.name = 'Success'
          )
    );
$$ LANGUAGE sql STABLE;

-- Strict ancestors of p_node_id (excludes p_node_id itself), root-most
-- reachable last. Reused by job_node_ready and
-- job_node_unsatisfied_prerequisites below to identify inherited
-- prerequisites (spec §6: "attached directly to that leaf or to any of
-- its ancestors").
CREATE FUNCTION job_node_ancestors(p_node_id bigint) RETURNS TABLE(id bigint) AS
$$
    WITH RECURSIVE ancestry(id) AS (
        SELECT parent_id FROM job_node WHERE id = p_node_id
        UNION ALL
        SELECT jn.parent_id FROM job_node jn JOIN ancestry a ON jn.id = a.id WHERE jn.parent_id IS NOT NULL
    )
    SELECT id FROM ancestry WHERE id IS NOT NULL;
$$ LANGUAGE sql STABLE;

-- Strict descendants of p_node_id (excludes p_node_id itself); the
-- subtree-traversal half of §6.5's "subtree and ancestry traversal"
-- canonical query.
CREATE FUNCTION job_node_descendants(p_node_id bigint) RETURNS TABLE(id bigint) AS
$$
    WITH RECURSIVE subtree(id) AS (
        SELECT id FROM job_node WHERE parent_id = p_node_id
        UNION ALL
        SELECT jn.id FROM job_node jn JOIN subtree s ON jn.parent_id = s.id
    )
    SELECT id FROM subtree;
$$ LANGUAGE sql STABLE;

-- For each requested leaf, determine whether p_actor_id owns that leaf or
-- any ancestor. The origin id keeps all ancestry walks in one set-based
-- invocation; callers use this to derive per-row session-management
-- capabilities without one ancestor query per leaf (ADR 0044).
CREATE FUNCTION job_node_controlled_leaf_ids(p_actor_id bigint, p_leaf_ids bigint[])
RETURNS TABLE(controlled_leaf_id bigint) AS
$$
    WITH RECURSIVE ancestors(origin_leaf_id, id, owner_user_id, parent_id) AS (
        SELECT jn.id, jn.id, jn.owner_user_id, jn.parent_id
        FROM job_node jn
        WHERE jn.id = ANY(p_leaf_ids)
        UNION ALL
        SELECT a.origin_leaf_id, jn.id, jn.owner_user_id, jn.parent_id
        FROM job_node jn
        JOIN ancestors a ON jn.id = a.parent_id
    )
    SELECT DISTINCT a.origin_leaf_id AS controlled_leaf_id
    FROM ancestors a
    WHERE a.owner_user_id = p_actor_id;
$$ LANGUAGE sql STABLE;

-- For each requested root, its complete descendant subtree (root included),
-- each row carrying its own parent_id, owner_user_id, and leaf achievement --
-- exactly the facts CostQueryAssembly needs, scoped to the requested subtree
-- rather than the whole job_node table (2026-07-24 code-review-scalability-
-- remediation-plan §2.2). origin_root_id lets a bulk request with several
-- roots at once distinguish which requested root(s) actually exist -- a root
-- id absent from job_node contributes no seed row and so never appears as an
-- origin_root_id. DISTINCT because overlapping requested subtrees (one root
-- an ancestor of another) legitimately revisit the same node from multiple
-- origins.
CREATE FUNCTION job_node_subtrees(p_root_ids bigint[])
RETURNS TABLE(origin_root_id bigint, id bigint, parent_id bigint, owner_user_id bigint, achievement_id smallint) AS
$$
    WITH RECURSIVE subtree(origin_root_id, id) AS (
        SELECT jn.id, jn.id
        FROM job_node jn
        WHERE jn.id = ANY(p_root_ids)
        UNION ALL
        SELECT s.origin_root_id, jn.id
        FROM job_node jn
        JOIN subtree s ON jn.parent_id = s.id
    )
    SELECT DISTINCT s.origin_root_id, s.id, jn.parent_id, jn.owner_user_id, lw.achievement_id
    FROM subtree s
    JOIN job_node jn ON jn.id = s.id
    LEFT JOIN leaf_work lw ON lw.job_node_id = s.id;
$$ LANGUAGE sql STABLE;

-- Every requested node's own ancestor chain up to the root (each node
-- included), each row carrying id/parent_id/owner_user_id. Complements
-- job_node_subtrees above: a cost read's contributing workers can have
-- sessions on leaves anywhere in the tree, not only inside the requested
-- subtree (ADR 0017's elevated read scope for a correct concurrency
-- divisor), and RateResolver's nearest-ancestor-override walk needs every
-- such leaf's own path to the root, plus the requested root's own path
-- above itself (a rate override can be declared on an ancestor of the
-- requested subtree). DISTINCT because multiple requested leaves commonly
-- share the same upper ancestors.
CREATE FUNCTION job_node_ancestor_chains(p_leaf_ids bigint[])
RETURNS TABLE(id bigint, parent_id bigint, owner_user_id bigint) AS
$$
    WITH RECURSIVE ancestors(id, parent_id, owner_user_id) AS (
        SELECT jn.id, jn.parent_id, jn.owner_user_id
        FROM job_node jn
        WHERE jn.id = ANY(p_leaf_ids)
        UNION ALL
        SELECT jn.id, jn.parent_id, jn.owner_user_id
        FROM job_node jn
        JOIN ancestors a ON jn.id = a.parent_id
    )
    SELECT DISTINCT id, parent_id, owner_user_id FROM ancestors;
$$ LANGUAGE sql STABLE;

-- Readiness (spec §6): p_node_id is ready iff every prerequisite declared
-- directly on it or inherited from any ancestor has a succeeded required
-- job.
CREATE FUNCTION job_node_ready(p_node_id bigint) RETURNS boolean AS
$$
    SELECT NOT EXISTS (
        SELECT 1
        FROM job_prerequisite jp
        WHERE jp.to_id IN (SELECT p_node_id UNION SELECT id FROM job_node_ancestors(p_node_id))
          AND NOT node_succeeded(jp.from_id)
    );
$$ LANGUAGE sql STABLE;

-- Readiness diagnostic (spec §6: "Readiness diagnostics shall identify the
-- prerequisite edge and the ancestor at which an inherited prerequisite
-- was declared"). declared_at_node_id is p_node_id itself for a directly
-- attached prerequisite, or the specific ancestor that declared an
-- inherited one.
CREATE FUNCTION job_node_unsatisfied_prerequisites(p_node_id bigint)
RETURNS TABLE(declared_at_node_id bigint, required_job_id bigint) AS
$$
    SELECT jp.to_id AS declared_at_node_id, jp.from_id AS required_job_id
    FROM job_prerequisite jp
    WHERE jp.to_id IN (SELECT p_node_id UNION SELECT id FROM job_node_ancestors(p_node_id))
      AND NOT node_succeeded(jp.from_id);
$$ LANGUAGE sql STABLE;
