-- Schema version 0005 (SQLite): job_node hierarchy acyclicity. See impl
-- plan §6.2 item 5, spec §4.1/§11 line 626/652, ADR 0012.
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
-- SQLite has no deferred constraint triggers and no advisory locks (spec
-- line 652). It does not need either here: SQLite is single-writer, so two
-- concurrent connections attempting opposing reparents already serialize
-- through SQLite's own file locking (with the busy_timeout PRAGMA the test
-- fixtures configure per connection) -- unlike PostgreSQL, where two
-- concurrent updates to different rows do not block each other. Whichever
-- move commits first changes the tree; the immediate trigger below then
-- evaluates the second move against that already-committed state and
-- correctly rejects it if it would close a cycle. There is therefore no
-- SQLite equivalent of PostgreSQL's move_job_node stored function -- the
-- "move" is the plain parameterized UPDATE that job_node_no_cycle guards:
--   UPDATE job_node SET parent_id = @newParentId, row_version = row_version + 1
--   WHERE id = @nodeId;

-- Walks existing ancestors of the new parent, upward via parent_id, not
-- descendants of the moved node downward. By the time this AFTER UPDATE
-- trigger runs, NEW.id's own row already has its new parent_id applied; a
-- downward "descendants of NEW.id" walk would revisit that just-changed
-- edge and recurse forever whenever the move is in fact a cycle (the exact
-- case this trigger exists to catch). Every row this walk reads other than
-- the stop condition is a pre-existing, previously-validated link, so it
-- only ever terminates at the root or at NEW.id itself (the guard below
-- stops expansion the moment it reaches NEW.id, since continuing from
-- there would just re-traverse the new edge back to NEW.parent_id).
CREATE TRIGGER job_node_no_cycle
    AFTER UPDATE OF parent_id ON job_node
    WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'moving job_node would create a cycle (parent/descendant)')
    WHERE NEW.id IN (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_id FROM job_node WHERE id = NEW.parent_id
            UNION ALL
            SELECT jn.parent_id
            FROM ancestors a
            JOIN job_node jn ON jn.id = a.id
            WHERE a.id IS NOT NULL AND a.id <> NEW.id
        )
        SELECT id FROM ancestors
    );
END;
