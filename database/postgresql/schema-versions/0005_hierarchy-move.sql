-- Schema version 0005 (PostgreSQL): job_node hierarchy acyclicity and atomic
-- move semantics. See impl plan §6.2 item 5, spec §4.1/§11 line 626,
-- ADR 0012.
--
-- Reachability from the root is not a new mechanism here -- it is a
-- corollary of already-established invariants. Slice 4's FK
-- (parent_id REFERENCES job_node ON DELETE RESTRICT) requires every
-- non-root row's parent to already exist, and job_node_single_root_idx
-- limits the whole table to at most one row with a null parent_id. A
-- finite graph where every node but one has exactly one outgoing parent
-- edge, that one node has none, and there are no cycles, is necessarily a
-- tree rooted at that node. So once this script's acyclicity guard is in
-- place, every node is reachable from the root for free.
--
-- Revalidation of prerequisite edges affected by a move (the other half of
-- item 5) is out of scope here -- job_prerequisite does not exist until
-- schema slice 8 (impl plan §6.2 item 8) and will be addressed then.
--
-- A plain deferred constraint trigger is not sufficient by itself to
-- prevent cycles under concurrency: two concurrent opposing reparents
-- (A's parent -> B, B's parent -> A) each touch a different row, so they
-- do not block each other, and each transaction's deferred check can run
-- against the other's not-yet-committed change and see stale, individually
-- valid state -- the same MVCC write-skew already proven for the
-- prerequisite graph in spike 02-prerequisite-cycle.sql. ADR 0012 commits
-- to the fix for this exact domain ("subtree move / decomposition"):
-- serialize concurrent moves behind a transaction-scoped advisory lock,
-- keyed on the moving node and its ancestors and the destination parent and
-- its ancestors, acquired in a single ascending-key order to avoid deadlock
-- (matching spike 04-advisory-lock-ordering.sql). move_job_node below is
-- that lock domain's only call site.

CREATE FUNCTION check_job_node_no_cycle() RETURNS trigger AS
$$
DECLARE
    cycle_exists boolean;
BEGIN
    IF NEW.parent_id IS NULL THEN
        RETURN NULL;
    END IF;

    -- Walk existing ancestors of the new parent, upward via parent_id, not
    -- descendants of the moved node downward. By the time this AFTER
    -- UPDATE trigger runs, NEW.id's own row already has its new parent_id
    -- applied; a downward "descendants of NEW.id" walk would revisit that
    -- just-changed edge and recurse forever whenever the move is in fact
    -- a cycle (the exact case this trigger exists to catch) -- every row
    -- this walk reads other than the stop condition is a pre-existing,
    -- previously-validated link, so it only ever terminates at the root or
    -- at NEW.id itself (the guard below stops expansion the moment it
    -- reaches NEW.id, since continuing from there would just re-traverse
    -- the new edge back to NEW.parent_id).
    WITH RECURSIVE ancestors(id) AS (
        SELECT parent_id FROM job_node WHERE id = NEW.parent_id
        UNION ALL
        SELECT jn.parent_id
        FROM ancestors a
        JOIN job_node jn ON jn.id = a.id
        WHERE a.id IS NOT NULL AND a.id <> NEW.id
    )
    SELECT EXISTS (SELECT 1 FROM ancestors WHERE id = NEW.id) INTO cycle_exists;

    IF cycle_exists THEN
        RAISE EXCEPTION 'moving job_node % under % would create a cycle', NEW.id, NEW.parent_id
            USING ERRCODE = 'P0003';
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Deferred per spec line 626: hierarchy acyclicity should use a deferred
-- constraint trigger so a valid multi-step structural operation in one
-- transaction can complete before the final check.
CREATE CONSTRAINT TRIGGER job_node_no_cycle
    AFTER UPDATE OF parent_id ON job_node
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    WHEN (NEW.parent_id IS NOT NULL)
EXECUTE FUNCTION check_job_node_no_cycle();

-- ADR 0012 lock-key construction: a fixed namespace constant XORed with the
-- contended entity's id, never a bare literal chosen ad hoc per call site.
CREATE FUNCTION jobtrack_subtree_move_lock_key(p_node_id bigint) RETURNS bigint AS
$$
SELECT hashtext('jobtrack:subtree-move')::bigint # p_node_id;
$$ LANGUAGE sql IMMUTABLE;

-- Atomic, concurrency-safe reparent: locks the moving node, its current
-- ancestors, the destination parent, and the destination parent's
-- ancestors (ADR 0012's "subtree move" lock domain), in ascending lock-key
-- order, before performing the reparent. Optimistic-concurrency
-- enforcement (comparing an expected row_version) is deliberately not done
-- here -- that is an EF/Persistence-layer concern for the library phase,
-- not this schema-phase primitive. This function unconditionally reparents
-- and bumps row_version; job_node_no_cycle above remains the authoritative
-- acyclicity check under concurrency.
--
-- A move and a concurrent job_prerequisite edge insert are a second,
-- cross-domain write-skew hazard beyond the two proven by the spikes: a
-- move that is individually acyclic and a new edge that is individually
-- not yet a hierarchy edge can each pass their own deferred check under
-- READ COMMITTED (neither sees the other's uncommitted change) and both
-- commit, jointly leaving a prerequisite edge whose endpoints are now
-- ancestor/descendant (impl plan §6.2 item 5's "a move can newly violate
-- the ancestor/descendant prohibition"). Proven by
-- Concurrent_move_and_prerequisite_edge_that_would_jointly_violate_ancestor_descendant_exclusion_allow_exactly_one_to_succeed
-- in JobPrerequisiteSchemaContractTestsBase, which failed on ~90% of runs
-- before this lock was added. The fix serializes move_job_node against
-- add_job_prerequisite (schema slice 8) by having both acquire the same
-- fixed 'jobtrack:prerequisite-graph-writes' advisory lock; move_job_node
-- acquires it last, after its own ascending-ordered subtree-move locks, so
-- no other call site ever waits on a subtree-move lock while holding this
-- one, and no cycle is introduced.
CREATE FUNCTION move_job_node(p_node_id bigint, p_new_parent_id bigint) RETURNS void AS
$$
DECLARE
    v_lock_key bigint;
BEGIN
    FOR v_lock_key IN
        WITH RECURSIVE node_chain(id) AS (
            SELECT p_node_id
            UNION ALL
            SELECT jn.parent_id
            FROM job_node jn
            JOIN node_chain c ON jn.id = c.id
            WHERE jn.parent_id IS NOT NULL
        ),
        parent_chain(id) AS (
            SELECT p_new_parent_id
            UNION ALL
            SELECT jn.parent_id
            FROM job_node jn
            JOIN parent_chain c ON jn.id = c.id
            WHERE jn.parent_id IS NOT NULL
        ),
        contended_ids(id) AS (
            SELECT id FROM node_chain
            UNION
            SELECT id FROM parent_chain
        )
        SELECT DISTINCT jobtrack_subtree_move_lock_key(id)
        FROM contended_ids
        ORDER BY 1
    LOOP
        PERFORM pg_advisory_xact_lock(v_lock_key);
    END LOOP;

    PERFORM pg_advisory_xact_lock(hashtext('jobtrack:prerequisite-graph-writes')::bigint);

    UPDATE job_node
    SET parent_id = p_new_parent_id, row_version = row_version + 1
    WHERE id = p_node_id;
END;
$$ LANGUAGE plpgsql;
