-- Schema version 0008 (SQLite): job_prerequisite, DAG enforcement, and
-- hierarchy-edge exclusion, revalidated on move. See impl plan §6.2 item 8,
-- spec §6, spec §11 line 652.
--
-- Direction: from_id is the required job, to_id is the dependent job
-- (spec §6: "RequiredJob -> DependentJob").
--
-- Readiness/eligibility queries need achievement derivation (schema slice
-- 13) and are out of scope here -- see the sibling PostgreSQL script's
-- header for the full rule list. SQLite has no deferred constraint
-- triggers or advisory locks, so all three checks below are immediate
-- triggers; SQLite's single-writer model serializes concurrent edge
-- inserts without an advisory-lock equivalent (as already noted for
-- concurrent moves in 0005).

CREATE TABLE job_prerequisite
(
    from_id INTEGER NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    to_id   INTEGER NOT NULL REFERENCES job_node (id) ON DELETE RESTRICT,
    PRIMARY KEY (from_id, to_id),
    CHECK (from_id <> to_id)
) STRICT, WITHOUT ROWID;

CREATE INDEX job_prerequisite_to_id_idx ON job_prerequisite (to_id);

-- Rule 4: acyclicity. Walks forward from the new edge's dependent node
-- (to_id); if it can already reach the required node (from_id), the new
-- edge would close a cycle.
CREATE TRIGGER job_prerequisite_no_cycle
    AFTER INSERT
    ON job_prerequisite
BEGIN
    SELECT RAISE(ABORT, 'prerequisite edge would create a cycle')
    WHERE NEW.from_id IN (
        WITH RECURSIVE reachable(node_id) AS (
            SELECT to_id FROM job_prerequisite WHERE from_id = NEW.to_id
            UNION
            SELECT jp.to_id FROM job_prerequisite jp JOIN reachable r ON jp.from_id = r.node_id
        )
        SELECT node_id FROM reachable
    );
END;

-- Rule 5 (edge side): reject a new edge whose endpoints are already
-- ancestor/descendant of each other in job_node.
CREATE TRIGGER job_prerequisite_not_hierarchy_edge
    AFTER INSERT
    ON job_prerequisite
BEGIN
    SELECT RAISE(ABORT, 'prerequisite edge is prohibited: endpoints are ancestor/descendant in the job hierarchy')
    WHERE NEW.from_id IN (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_id FROM job_node WHERE id = NEW.to_id
            UNION ALL
            SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
        )
        SELECT id FROM ancestors
    )
    OR NEW.to_id IN (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_id FROM job_node WHERE id = NEW.from_id
            UNION ALL
            SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
        )
        SELECT id FROM ancestors
    );
END;

-- Rule 5 (move side): re-validate every existing prerequisite edge
-- whenever job_node.parent_id changes (immediate, per-statement, since
-- SQLite has no deferred constraint triggers).
CREATE TRIGGER job_prerequisite_edges_after_move
    AFTER UPDATE OF parent_id
    ON job_node
    WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'moving job_node would leave a prerequisite edge as an ancestor/descendant edge')
    WHERE EXISTS (
        SELECT 1
        FROM job_prerequisite jp
        WHERE jp.from_id IN (
            WITH RECURSIVE ancestors(id) AS (
                SELECT parent_id FROM job_node WHERE id = jp.to_id
                UNION ALL
                SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
            )
            SELECT id FROM ancestors
        )
        OR jp.to_id IN (
            WITH RECURSIVE ancestors(id) AS (
                SELECT parent_id FROM job_node WHERE id = jp.from_id
                UNION ALL
                SELECT jn.parent_id FROM job_node jn JOIN ancestors a ON jn.id = a.id WHERE a.id IS NOT NULL
            )
            SELECT id FROM ancestors
        )
    );
END;
