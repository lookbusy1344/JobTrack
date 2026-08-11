-- Spike 4: transaction-scoped advisory-lock serialization for subtree
-- moves, with deterministic lock ordering to avoid deadlock. Throwaway
-- proof for plan §5.3 bullet 4 / ADR 0012. Not production schema.

-- Simulates a "move" touching two nodes (source parent, destination
-- parent) that must both be locked for the duration of the move.
-- Deterministic ordering: always acquire the numerically smaller key
-- first, per ADR 0012.
CREATE OR REPLACE FUNCTION move_node_locked(p_node_a bigint, p_node_b bigint, p_delay_seconds numeric DEFAULT 0.5) RETURNS text AS $$
DECLARE
    lock_key_a bigint := p_node_a;
    lock_key_b bigint := p_node_b;
    first_key bigint;
    second_key bigint;
BEGIN
    IF lock_key_a <= lock_key_b THEN
        first_key := lock_key_a; second_key := lock_key_b;
    ELSE
        first_key := lock_key_b; second_key := lock_key_a;
    END IF;

    PERFORM pg_advisory_xact_lock(first_key);
    PERFORM pg_sleep(p_delay_seconds);
    PERFORM pg_advisory_xact_lock(second_key);

    RETURN format('moved holding locks %s then %s', first_key, second_key);
END;
$$ LANGUAGE plpgsql;
