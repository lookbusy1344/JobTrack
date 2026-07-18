-- Spike 2: prerequisite-cycle detection under concurrent edge insertion.
-- Throwaway proof for plan §5.3 bullet 2. Not production schema.

DROP TABLE IF EXISTS job_prerequisite CASCADE;

CREATE TABLE job_prerequisite (
    from_id bigint NOT NULL,
    to_id   bigint NOT NULL,
    PRIMARY KEY (from_id, to_id),
    CHECK (from_id <> to_id)
);

-- Deferred constraint trigger: after inserting an edge, check whether
-- to_id can reach from_id (which would close a cycle through the new
-- edge), using a recursive CTE over currently-visible (committed, at the
-- point this transaction's snapshot/read applies) edges.
CREATE OR REPLACE FUNCTION check_no_prerequisite_cycle() RETURNS trigger AS $$
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
        RAISE EXCEPTION 'invariant violation: prerequisite edge %->% would create a cycle', NEW.from_id, NEW.to_id
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_no_prerequisite_cycle
    AFTER INSERT ON job_prerequisite
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION check_no_prerequisite_cycle();

-- Mitigation: serialize all prerequisite-edge writes with a fixed
-- transaction-scoped advisory lock (ADR 0012's "prerequisite-graph
-- writes" lock domain), acquired before the insert. This is what the
-- concurrent test below shows is actually necessary.
CREATE OR REPLACE FUNCTION add_prerequisite_edge_locked(p_from bigint, p_to bigint) RETURNS void AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('jobtrack:prerequisite-graph-writes')::bigint);
    INSERT INTO job_prerequisite (from_id, to_id) VALUES (p_from, p_to);
END;
$$ LANGUAGE plpgsql;
