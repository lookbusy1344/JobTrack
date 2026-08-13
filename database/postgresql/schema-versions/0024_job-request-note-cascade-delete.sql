-- Schema version 0024 (PostgreSQL): ADR 0068 qualifies job_request_note's
-- "append-only" against ADR 0061's recursive node deletion -- a note
-- outlives every ordinary operation, but not the request it belongs to.
-- Originally landed as an in-place edit of the already-deployed schema
-- version 0020, which ADR 0011's forward-only rule forbids once a real
-- deployment exists; this is that change carried forward instead.
--
-- The foreign key becomes ON DELETE CASCADE and the reject-delete trigger
-- fires only while the parent job_request row is still there -- during the
-- cascade PostgreSQL has already removed the parent, so the trigger's
-- EXISTS finds nothing and lets the note go. A note can consequently never
-- be deleted on its own, only as part of destroying the whole request.

ALTER TABLE job_request_note
    DROP CONSTRAINT job_request_note_job_node_id_fkey,
    ADD CONSTRAINT job_request_note_job_node_id_fkey
        FOREIGN KEY (job_node_id) REFERENCES job_request (job_node_id) ON DELETE CASCADE;

CREATE OR REPLACE FUNCTION reject_job_request_note_delete() RETURNS trigger AS
$$
BEGIN
    IF EXISTS (SELECT 1 FROM job_request WHERE job_node_id = OLD.job_node_id) THEN
        RAISE EXCEPTION 'job_request_note rows are append-only and cannot be deleted while their request exists';
    END IF;

    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
