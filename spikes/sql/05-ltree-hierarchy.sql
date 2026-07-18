-- Spike 5: does PostgreSQL's ltree extension meaningfully beat the current
-- adjacency-list + recursive-CTE hierarchy queries at representative
-- scale? Throwaway proof for the PostgreSQL column-type remediation plan
-- (docs/plans/2026-07-11-postgresql-column-type-remediation-plan.md §3.2).
-- Not production schema -- run against jobtrack_spike, never against the
-- real database.
--
-- Builds one table carrying BOTH an adjacency-list parent_id and a
-- maintained ltree path column, seeded at a shape matching the "combined
-- production tree" performance-budget scale (docs/traceability/
-- performance-budgets.md §1: ~193,500 nodes, branching [10,5,6,7,7] then
-- 12 leaves per depth-5 branch, plus a 9-level single-child chain off one
-- leaf reaching depth 15) -- close enough in order of magnitude and shape
-- to be representative without reproducing JobTrack.TestSupport's exact
-- generator byte-for-byte.
--
-- Every query below is run twice: once as the recursive-CTE form the real
-- schema uses today (job_node_ancestors/job_node_descendants, schema
-- version 0013), once as the ltree-operator equivalent, both under
-- EXPLAIN (ANALYZE, BUFFERS). Compare planning/execution time and plan
-- shape (seq scan vs index scan) directly from the two EXPLAIN outputs.

CREATE EXTENSION IF NOT EXISTS ltree;

DROP TABLE IF EXISTS ltree_spike_node;
CREATE TABLE ltree_spike_node
(
    id        bigint PRIMARY KEY,
    parent_id bigint REFERENCES ltree_spike_node (id),
    path      ltree NOT NULL
);

CREATE INDEX ltree_spike_node_parent_id_idx ON ltree_spike_node (parent_id);
CREATE INDEX ltree_spike_node_path_gist_idx ON ltree_spike_node USING gist (path);
CREATE INDEX ltree_spike_node_path_btree_idx ON ltree_spike_node USING btree (path);

DROP SEQUENCE IF EXISTS ltree_spike_id_seq;
CREATE SEQUENCE ltree_spike_id_seq;

-- Level 1: 10 root children (the spike root itself is not part of the
-- measured tree -- job_node_ancestors excludes self and the real schema's
-- root is a single fixed node, not the branching point).
WITH new_rows AS (
    SELECT nextval('ltree_spike_id_seq') AS id
    FROM generate_series(1, 10)
)
INSERT INTO ltree_spike_node (id, parent_id, path)
SELECT id, NULL, ('n' || id)::ltree FROM new_rows;

-- Level 2: x5 per level-1 node (50 nodes).
WITH parents AS (SELECT id, path FROM ltree_spike_node WHERE parent_id IS NULL),
     new_rows AS (
         SELECT nextval('ltree_spike_id_seq') AS id, p.id AS parent_id, p.path AS parent_path
         FROM parents p CROSS JOIN generate_series(1, 5)
     )
INSERT INTO ltree_spike_node (id, parent_id, path)
SELECT id, parent_id, parent_path || ('n' || id)::ltree FROM new_rows;

-- Level 3: x6 per level-2 node (300 nodes).
WITH parents AS (
    SELECT n.id, n.path FROM ltree_spike_node n
    WHERE nlevel(n.path) = 2
),
new_rows AS (
    SELECT nextval('ltree_spike_id_seq') AS id, p.id AS parent_id, p.path AS parent_path
    FROM parents p CROSS JOIN generate_series(1, 6)
)
INSERT INTO ltree_spike_node (id, parent_id, path)
SELECT id, parent_id, parent_path || ('n' || id)::ltree FROM new_rows;

-- Level 4: x7 per level-3 node (2,100 nodes).
WITH parents AS (
    SELECT n.id, n.path FROM ltree_spike_node n
    WHERE nlevel(n.path) = 3
),
new_rows AS (
    SELECT nextval('ltree_spike_id_seq') AS id, p.id AS parent_id, p.path AS parent_path
    FROM parents p CROSS JOIN generate_series(1, 7)
)
INSERT INTO ltree_spike_node (id, parent_id, path)
SELECT id, parent_id, parent_path || ('n' || id)::ltree FROM new_rows;

-- Level 5: x7 per level-4 node (14,700 nodes).
WITH parents AS (
    SELECT n.id, n.path FROM ltree_spike_node n
    WHERE nlevel(n.path) = 4
),
new_rows AS (
    SELECT nextval('ltree_spike_id_seq') AS id, p.id AS parent_id, p.path AS parent_path
    FROM parents p CROSS JOIN generate_series(1, 7)
)
INSERT INTO ltree_spike_node (id, parent_id, path)
SELECT id, parent_id, parent_path || ('n' || id)::ltree FROM new_rows;

-- Level 6 (leaves): x12 per level-5 node (176,400 nodes). Total: 193,560.
WITH parents AS (
    SELECT n.id, n.path FROM ltree_spike_node n
    WHERE nlevel(n.path) = 5
),
new_rows AS (
    SELECT nextval('ltree_spike_id_seq') AS id, p.id AS parent_id, p.path AS parent_path
    FROM parents p CROSS JOIN generate_series(1, 12)
)
INSERT INTO ltree_spike_node (id, parent_id, path)
SELECT id, parent_id, parent_path || ('n' || id)::ltree FROM new_rows;

-- 9-level single-child chain off one arbitrary leaf, reaching depth 15
-- (matching the combined-production-tree scale's max depth).
DO $$
DECLARE
    current_id   bigint;
    current_path ltree;
    new_id       bigint;
BEGIN
    SELECT id, path INTO current_id, current_path
    FROM ltree_spike_node
    WHERE nlevel(path) = 6
    ORDER BY id
    LIMIT 1;

    FOR i IN 1..9 LOOP
        new_id := nextval('ltree_spike_id_seq');
        INSERT INTO ltree_spike_node (id, parent_id, path)
        VALUES (new_id, current_id, current_path || ('n' || new_id)::ltree);
        current_id := new_id;
        SELECT path INTO current_path FROM ltree_spike_node WHERE id = current_id;
    END LOOP;

    RAISE NOTICE 'deep-chain leaf id = %, depth = %', current_id, nlevel(current_path);
END $$;

ANALYZE ltree_spike_node;

SELECT count(*) AS total_nodes, max(nlevel(path)) AS max_depth FROM ltree_spike_node;

-- ==========================================================================
-- Query A: ancestor traversal of the depth-15 chain node (mirrors
-- job_node_ancestors + the deep-tree performance test, budget 50ms).
-- ==========================================================================

-- Target materialized once via a psql variable so neither form below pays
-- for re-locating "the deepest node" per reference (a spike-only lookup;
-- real callers already have the node id in hand).
SELECT id AS target_id, path AS target_path
FROM ltree_spike_node ORDER BY nlevel(path) DESC LIMIT 1 \gset

\echo '--- A1: recursive-CTE ancestors (adjacency list) ---'
EXPLAIN (ANALYZE, BUFFERS)
WITH RECURSIVE ancestry(id) AS (
    SELECT parent_id FROM ltree_spike_node WHERE id = :target_id
    UNION ALL
    SELECT jn.parent_id FROM ltree_spike_node jn JOIN ancestry a ON jn.id = a.id WHERE jn.parent_id IS NOT NULL
)
SELECT id FROM ancestry WHERE id IS NOT NULL;

\echo '--- A2: ltree ancestors (path @> target, GiST index) ---'
EXPLAIN (ANALYZE, BUFFERS)
SELECT id FROM ltree_spike_node
WHERE path @> :'target_path'::ltree AND id <> :target_id;

-- ==========================================================================
-- Query B: descendant traversal of a broad level-1 subtree (~19,356 nodes;
-- mirrors job_node_descendants / node_succeeded's subtree walk, budget
-- 100ms at this kind of branch-scoped scale).
-- ==========================================================================

SELECT id AS branch_id, path AS branch_path FROM ltree_spike_node WHERE parent_id IS NULL ORDER BY id LIMIT 1 \gset

\echo '--- B1: recursive-CTE descendants (adjacency list) ---'
EXPLAIN (ANALYZE, BUFFERS)
WITH RECURSIVE subtree(id) AS (
    SELECT id FROM ltree_spike_node WHERE parent_id = :branch_id
    UNION ALL
    SELECT jn.id FROM ltree_spike_node jn JOIN subtree s ON jn.parent_id = s.id
)
SELECT id FROM subtree;

\echo '--- B2: ltree descendants (path <@ target, GiST index) ---'
EXPLAIN (ANALYZE, BUFFERS)
SELECT id FROM ltree_spike_node
WHERE path <@ :'branch_path'::ltree AND id <> :branch_id;

-- ==========================================================================
-- Query C: nearest-ancestor lookup pattern (mirrors resolve_rate's
-- depth-ordered chain(id, depth) walk joined to node_rate_override).
-- Seeds a handful of "override" rows at scattered ancestor depths along
-- the deep chain, then finds the shallowest (nearest) one for the
-- deepest leaf.
-- ==========================================================================

DROP TABLE IF EXISTS ltree_spike_override;
CREATE TABLE ltree_spike_override (node_id bigint PRIMARY KEY);
INSERT INTO ltree_spike_override (node_id)
SELECT id FROM ltree_spike_node WHERE nlevel(path) IN (1, 3, 6, 10) ORDER BY id LIMIT 4;
ANALYZE ltree_spike_override;

\echo '--- C1: recursive-CTE chain(id, depth) + join (adjacency list) ---'
EXPLAIN (ANALYZE, BUFFERS)
WITH RECURSIVE chain(id, depth) AS (
    SELECT :target_id::bigint, 0
    UNION ALL
    SELECT jn.parent_id, c.depth + 1
    FROM ltree_spike_node jn JOIN chain c ON jn.id = c.id
    WHERE jn.parent_id IS NOT NULL
)
SELECT o.node_id
FROM ltree_spike_override o
JOIN chain c ON c.id = o.node_id
ORDER BY c.depth
LIMIT 1;

\echo '--- C2: ltree nearest-ancestor (path @> target, ORDER BY nlevel DESC) ---'
EXPLAIN (ANALYZE, BUFFERS)
SELECT o.node_id
FROM ltree_spike_override o
JOIN ltree_spike_node n ON n.id = o.node_id
WHERE n.path @> :'target_path'::ltree
ORDER BY nlevel(n.path) DESC
LIMIT 1;
