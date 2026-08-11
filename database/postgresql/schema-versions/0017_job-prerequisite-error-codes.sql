-- Schema version 0017 (PostgreSQL): distinct SQLSTATEs for job_prerequisite's
-- two deferred constraint triggers. See impl plan §7.4, mirroring schema
-- version 0016's rationale for move_job_node's 'P0003'/'P0004' split.
--
-- Schema slice 8's check_job_prerequisite_no_cycle and
-- check_job_prerequisite_not_hierarchy_edge each RAISE EXCEPTION with no
-- ERRCODE, so both default to the same generic 'P0001' -- indistinguishable
-- from each other by SQLSTATE alone. Now that the library phase's
-- AddPrerequisiteAsync must translate each to its own stable
-- InvariantViolationException.ConstraintId ("job-prerequisite-would-cycle"
-- vs. "job-prerequisite-is-hierarchy-edge"), each gets its own code:
-- 'P0005' for the cycle check, 'P0006' for the hierarchy-edge check. Purely
-- additive -- CREATE OR REPLACE FUNCTION keeps each existing trigger's
-- definition (and lock/ordering behaviour) otherwise unchanged.

CREATE OR REPLACE FUNCTION check_job_prerequisite_no_cycle() RETURNS trigger AS
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
        RAISE EXCEPTION 'prerequisite edge % -> % would create a cycle', NEW.from_id, NEW.to_id
            USING ERRCODE = 'P0005';
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION check_job_prerequisite_not_hierarchy_edge() RETURNS trigger AS
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
            NEW.from_id, NEW.to_id
            USING ERRCODE = 'P0006';
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;
