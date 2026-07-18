-- Counterfactual: what if "at most one root" were enforced ONLY by a
-- deferred count-based constraint trigger, with no partial unique index?
-- Demonstrates why ADR 0015 requires the unique index as the actual race
-- resolution mechanism, not merely the deferred trigger.

DROP TABLE IF EXISTS job_node_counterfactual CASCADE;

CREATE TABLE job_node_counterfactual (
    job_node_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    parent_id    bigint REFERENCES job_node_counterfactual (job_node_id)
);

CREATE OR REPLACE FUNCTION check_at_most_one_root_cf() RETURNS trigger AS $$
DECLARE
    root_count int;
BEGIN
    SELECT count(*) INTO root_count FROM job_node_counterfactual WHERE parent_id IS NULL;
    IF root_count > 1 THEN
        RAISE EXCEPTION 'invariant violation: at most one root required, found %', root_count
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_at_most_one_root_cf
    AFTER INSERT ON job_node_counterfactual
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION check_at_most_one_root_cf();
