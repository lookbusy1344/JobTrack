-- Spike 1: single-root invariant under concurrent independent connections.
-- Throwaway proof for plan §5.3 bullet 1 / ADR 0015. Not production schema.

DROP TABLE IF EXISTS job_node CASCADE;

CREATE TABLE job_node (
    job_node_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    parent_id    bigint REFERENCES job_node (job_node_id),
    is_leaf      boolean NOT NULL DEFAULT false
);

-- "At most one root": a partial unique index over a constant expression,
-- restricted to root rows. This is the mechanism that actually resolves
-- the concurrent-insert race (a uniqueness violation blocks/rejects the
-- loser), which a count-based check cannot do under MVCC snapshot
-- isolation (see spike-report.md).
CREATE UNIQUE INDEX idx_job_node_single_root
    ON job_node ((true))
    WHERE parent_id IS NULL;

-- "At least one root" (and leaf/branch exclusivity would live alongside
-- this in the real schema, §6.2 item 4/6): a deferred constraint trigger,
-- since minimum-cardinality-one cannot be enforced by a row trigger on an
-- empty table, and because a valid multi-step operation (replacing the
-- root) may pass through a zero-root intermediate state within one
-- transaction.
CREATE OR REPLACE FUNCTION check_at_least_one_root() RETURNS trigger AS $$
DECLARE
    root_count int;
BEGIN
    SELECT count(*) INTO root_count FROM job_node WHERE parent_id IS NULL;
    IF root_count = 0 THEN
        RAISE EXCEPTION 'invariant violation: at least one root required, found 0'
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_at_least_one_root
    AFTER INSERT OR UPDATE OR DELETE ON job_node
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION check_at_least_one_root();
