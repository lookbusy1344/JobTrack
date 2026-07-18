-- Schema version 0016 (PostgreSQL): give move_job_node an expected-version
-- parameter so the persistence layer's optimistic-concurrency check for a
-- move shares the same atomic, lock-ordered transaction as the reparent
-- itself, rather than racing a separate application-side compare-and-swap
-- against the lock/cycle logic in schema slice 5. See impl plan §7.4,
-- ADR 0012.
--
-- Schema slice 5's move_job_node deliberately left this out ("optimistic-
-- concurrency enforcement... is deliberately not done here -- that is an
-- EF/Persistence-layer concern for the library phase"); now that the
-- library phase is implementing the move command, the CAS check belongs
-- inside the same lock-ordered function, not as a separate SELECT ... FOR
-- UPDATE beforehand -- a separate check would have to take its own row
-- lock to avoid a second race between the check and the function call,
-- duplicating exactly the serialization move_job_node already provides.
--
-- Concurrency conflicts are reported as a distinct SQLSTATE ('P0004') from
-- the cycle check's 'P0003' (schema slice 5) so the persistence layer can
-- translate each to its own JobTrackException subtype without parsing
-- message text.

DROP FUNCTION move_job_node(bigint, bigint);

CREATE FUNCTION move_job_node(p_node_id bigint, p_new_parent_id bigint, p_expected_version bigint) RETURNS void AS
$$
DECLARE
    v_lock_key   bigint;
    v_row_count  int;
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
    WHERE id = p_node_id AND row_version = p_expected_version;

    GET DIAGNOSTICS v_row_count = ROW_COUNT;

    IF v_row_count = 0 THEN
        RAISE EXCEPTION 'job_node % was not at expected version % (concurrency conflict)', p_node_id, p_expected_version
            USING ERRCODE = 'P0004';
    END IF;
END;
$$ LANGUAGE plpgsql;
