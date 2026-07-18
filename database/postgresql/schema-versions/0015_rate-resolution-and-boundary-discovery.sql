-- Schema version 0015 (PostgreSQL): rate-resolution precedence and
-- rate-boundary discovery. See impl plan §6.2 item 13, §6.5, spec
-- §9.1-§9.3, spec_claude Appendix C.4 (partially -- see below).
--
-- This is the third of three sub-slices covering impl plan item 13's five
-- query families; 13a (schema version 0013) covered hierarchy, achievement,
-- and readiness, and 13b (schema version 0014) covered worker overlap
-- discovery.
--
-- The full cost-input sweep (spec_claude Appendix C.4's `clip_to_working_set`
-- and the complete boundary-partition query) is deliberately NOT
-- implemented here. `clip_to_working_set` requires expanding a user's
-- recurring weekly schedule (user_schedule_interval, civil local times)
-- against calendar dates, which ADR 0008 commits to resolving exclusively
-- through Noda Time's ZoneLocalMapping with named, shared resolvers "wired
-- ... in the domain layer -- not reconstructed per call site". There is no
-- equivalent DST gap/fold resolution available in portable PostgreSQL SQL,
-- and reimplementing a simplified approximation here would silently
-- contradict that ADR. Effective working-interval clipping is therefore a
-- JobTrack.Domain deliverable (Phase 2, plan §7.2 item 5), not a database
-- schema slice.
--
-- What IS schema-appropriate and implemented here: every rate-affecting
-- range in this schema (user_cost_rate, node_rate_override,
-- user_schedule_exception.rate_override) is stored as a plain instant
-- range (timestamptz) with no timezone/civil-time interpretation step
-- (0011's own header note) -- resolve_rate and user_rate_boundaries below
-- are therefore pure, DST-independent, and correctly schema-layer.
--
-- Both functions below read the generated effective_range/exception_range
-- columns added to user_cost_rate, node_rate_override, and
-- user_schedule_exception (schema versions 0011/0010) rather than
-- reconstructing tstzrange(...) inline, per the PostgreSQL column-type
-- remediation plan (docs/plans/2026-07-11-postgresql-column-type-remediation-plan.md
-- §3.1). resolve_rate's point-in-time predicates are algebraically identical:
-- "effective_range @> p_at" is exactly "effective_start <= p_at AND
-- (effective_end IS NULL OR effective_end > p_at)" for a '[)' range with
-- infinity substituted for a NULL upper bound, since @> on a scalar tests
-- lower-inclusive/upper-exclusive containment. user_rate_boundaries'
-- "effective_range && tstzrange(p_from, p_to, '[)')" is exactly the prior
-- "effective_start < p_to AND COALESCE(effective_end, 'infinity') > p_from"
-- open-interval-intersection test, since && on two '[)' ranges reduces to
-- that same pair of strict inequalities. This lets both queries use the
-- GiST index already backing each table's exclusion constraint instead of
-- evaluating scalar comparisons against unindexed expressions.
--
-- resolve_rate implements spec §9.3's precedence order: an explicitly
-- priced additive schedule exception outranks a node-rate override
-- (nearest ancestor first, via the same ancestor-chain walk as 13a's
-- job_node_ancestors), which outranks the user's own effective-dated rate,
-- which outranks the user's default rate. A NULL result means no rate
-- resolves at all (spec §9.1: "otherwise costing shall report an explicit
-- missing-rate error") -- raising that error is an application-layer
-- concern, not this query contract's.
CREATE FUNCTION resolve_rate(p_node_id bigint, p_user_id bigint, p_at timestamptz) RETURNS numeric(19, 6) AS
$$
    WITH RECURSIVE chain(id, depth) AS (
        SELECT p_node_id, 0
        UNION ALL
        SELECT jn.parent_id, c.depth + 1
        FROM job_node jn
        JOIN chain c ON jn.id = c.id
        WHERE jn.parent_id IS NOT NULL
    ),
    exception_rate AS (
        SELECT rate_override AS rate
        FROM user_schedule_exception
        WHERE user_id = p_user_id
          AND effect_id = 1
          AND rate_override IS NOT NULL
          AND exception_range @> p_at
        LIMIT 1
    ),
    override_rate AS (
        SELECT nro.rate
        FROM node_rate_override nro
        JOIN chain c ON c.id = nro.node_id
        WHERE nro.user_id = p_user_id
          AND nro.effective_range @> p_at
        ORDER BY c.depth
        LIMIT 1
    ),
    user_rate AS (
        SELECT rate
        FROM user_cost_rate
        WHERE user_id = p_user_id
          AND effective_range @> p_at
        LIMIT 1
    ),
    default_rate AS (
        SELECT default_hourly_rate AS rate FROM app_user WHERE id = p_user_id
    )
    SELECT COALESCE(
        (SELECT rate FROM exception_rate),
        (SELECT rate FROM override_rate),
        (SELECT rate FROM user_rate),
        (SELECT rate FROM default_rate)
    );
$$ LANGUAGE sql STABLE;

-- Every rate-affecting range edge for (p_user_id, p_node_id) intersecting
-- [p_from, p_to), across all three rate sources resolve_rate reads from.
-- Fixes the exact gap plan §6.5 calls out in spec_claude Appendix C.4's
-- illustrative sketch: "omits node-override ancestor-chain boundaries" --
-- every ancestor of p_node_id that holds an override for p_user_id
-- contributes its boundaries here, not just p_node_id itself.
CREATE FUNCTION user_rate_boundaries(p_user_id bigint, p_node_id bigint, p_from timestamptz, p_to timestamptz)
RETURNS TABLE(effective tstzrange) AS
$$
    SELECT effective_range
    FROM user_cost_rate
    WHERE user_id = p_user_id
      AND effective_range && tstzrange(p_from, p_to, '[)')

    UNION ALL

    SELECT nro.effective_range
    FROM node_rate_override nro
    WHERE nro.user_id = p_user_id
      AND nro.node_id IN (SELECT p_node_id UNION SELECT id FROM job_node_ancestors(p_node_id))
      AND nro.effective_range && tstzrange(p_from, p_to, '[)')

    UNION ALL

    SELECT exception_range
    FROM user_schedule_exception
    WHERE user_id = p_user_id
      AND effect_id = 1
      AND rate_override IS NOT NULL
      AND exception_range && tstzrange(p_from, p_to, '[)');
$$ LANGUAGE sql STABLE;
