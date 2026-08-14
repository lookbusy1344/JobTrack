-- Schema version 0026 (PostgreSQL): a node_rate_override may not target the
-- permanent root. See ADR 0069, spec §4.2 invariant 11, §9.2.
--
-- An override applies to its node and every descendant (spec §9.2), so a root
-- override would price a worker's whole tree -- a restatement of that worker's
-- own rate, which user_cost_rate (§9.3 level 3) and the user default (level 4)
-- already express. schema version 0011's node_rate_override predates this rule
-- and is never edited in place (ADR 0011): the guard arrives as its own version.
--
-- Enforced on INSERT and on any UPDATE OF node_id -- the only writes that can
-- point an override at the root. A node targeted by an override can never
-- *become* the root, and the root can never *shed* its role: the single-root
-- partial unique index (schema 0004) and ADR 0015's permanent-root guard make
-- both impossible, so no trigger on job_node's own writes is needed.

CREATE FUNCTION reject_node_rate_override_on_root() RETURNS trigger AS
$$
BEGIN
    IF (SELECT parent_id FROM job_node WHERE id = NEW.node_id) IS NULL THEN
        RAISE EXCEPTION 'a node_rate_override cannot target the root node (ADR 0069)';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER node_rate_override_not_on_root_on_insert
    BEFORE INSERT
    ON node_rate_override
    FOR EACH ROW
EXECUTE FUNCTION reject_node_rate_override_on_root();

CREATE TRIGGER node_rate_override_not_on_root_on_update
    BEFORE UPDATE OF node_id
    ON node_rate_override
    FOR EACH ROW
EXECUTE FUNCTION reject_node_rate_override_on_root();
