-- Schema version 0018 (PostgreSQL): rewrite worker_overlapping_sessions's
-- predicate to be sargable against the GiST range index. See ADR 0010
-- (EF-first with stored-function encapsulation), which names both
-- "database-wide overlap discovery" and "the canonical cost-input queries"
-- as the sanctioned reason for this function to exist at all.
--
-- Schema version 0014 introduced this function with the predicate
--   s.started_at < p_query_end AND (s.finished_at IS NULL OR s.finished_at > p_query_start)
-- which is never sargable against a range index -- neither the two btree
-- composites on (worked_by_user_id, started_at)/(worked_by_user_id,
-- finished_at) nor work_session_user_range_gist_idx (the index schema
-- version 0007 added specifically for this access pattern, spec §11 line
-- 629) can push the date bound into the index scan once it is combined
-- with an OR/NULL check on a different column. In practice PostgreSQL
-- falls back to an index scan keyed on worked_by_user_id alone, then
-- filters every one of that worker's rows in memory -- invisible on a
-- worker with little history outside the query window, increasingly
-- costly as a worker's own history grows past it (measured: on a
-- 5,000-session/~208-day worker queried with a 10-hour window, 0.31 ms
-- and 89 block reads before this change, 0.009 ms and 4 block reads
-- after -- see docs/plans/2026-07-09-overlapping-cost-scale-plan.md and
-- docs/traceability/performance-budgets.md §2).
--
-- Rewritten to test overlap directly against the generated session_range
-- column (schema version 0007), which lets the planner push both the
-- worked_by_user_id equality and the range overlap into
-- work_session_user_range_gist_idx as a single index condition -- but only
-- once the planner's row-count estimate for that condition is reasonably
-- accurate; on a table just bulk-loaded with no ANALYZE yet run (this
-- migration's own measurement scenario, and PerformanceScaleGenerator's
-- seeded scales), stale/default statistics can still make one of the
-- plain btree composites look artificially cheaper even though this
-- rewrite makes the GiST plan available. A production database's
-- autovacuum daemon keeps statistics current as sessions accumulate
-- gradually, so this is a fixture-freshness concern, not a production
-- one; PerformanceScaleGenerator runs an explicit ANALYZE after seeding
-- to measure against realistic, not artificially stale, statistics.
-- Provably equivalent regardless: session_range = tstzrange(started_at,
-- COALESCE(finished_at, 'infinity'), '[)'), and two half-open intervals
-- [a,b) and [c,d) overlap iff a < d AND c < b -- substituting gives
-- exactly the original predicate.
--
-- One deliberate behaviour difference: tstzrange(p_query_start,
-- p_query_end, '[)') raises an error if p_query_start > p_query_end,
-- where the old predicate silently returned zero rows for inverted
-- bounds. Every real caller's bounds originate from a WorkInterval value
-- object (src/JobTrack.Domain/Intervals/WorkInterval.cs), which already
-- throws on construction if end <= start, so no real caller can supply
-- inverted bounds here -- consistent with this codebase's "exceptions are
-- the sole failure channel" convention rather than silently tolerating
-- caller error.
--
-- Purely additive otherwise -- CREATE OR REPLACE FUNCTION keeps the
-- signature and output columns unchanged; the boundary-exact behaviour
-- (touching-but-not-overlapping at either end, unfinished sessions,
-- cross-leaf same-user overlap) is unchanged, per
-- WorkerOverlapCandidateDiscoverySchemaContractTestsBase's existing
-- contract, run unchanged against this revision.

CREATE OR REPLACE FUNCTION worker_overlapping_sessions(
    p_user_id bigint, p_query_start timestamptz, p_query_end timestamptz, p_as_of timestamptz)
RETURNS TABLE(session_id bigint, leaf_work_id bigint, started_at timestamptz, finished_at timestamptz, effective_finished_at timestamptz) AS
$$
    SELECT s.id, s.leaf_work_id, s.started_at, s.finished_at, COALESCE(s.finished_at, p_as_of)
    FROM work_session s
    WHERE s.worked_by_user_id = p_user_id
      AND s.session_range && tstzrange(p_query_start, p_query_end, '[)');
$$ LANGUAGE sql STABLE;
