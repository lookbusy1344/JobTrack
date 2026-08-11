-- Schema version 0014 (PostgreSQL): worker-scoped, database-wide
-- overlap-candidate discovery. See impl plan §6.2 item 13, §6.5, spec
-- §10.2.2, ADR 0017.
--
-- This is the second of three sub-slices covering impl plan item 13's five
-- query families; slice 13a (schema version 0013) covered hierarchy,
-- achievement, and readiness. The full cost-input sweep (spec_claude
-- Appendix C.4, needing the schedule/rate helpers from slices 9-11)
-- remains the third sub-slice.
--
-- Per spec §10.2.2, a candidate session overlaps a finite query interval
-- [p_query_start, p_query_end) when:
--   worked_by_user_id = p_user_id
--   AND started_at < p_query_end
--   AND (finished_at IS NULL OR finished_at > p_query_start)
-- The predicate deliberately does not substitute p_as_of for a null
-- finished_at before filtering ("an unfinished session uses asOf as its
-- effective end after candidate retrieval") -- effective_finished_at below
-- exposes that asOf-clipped value as a separate column for the caller's
-- interval math, without changing which rows are considered candidates.
--
-- This function is deliberately unconditional across the whole database,
-- with no caller-authorization scoping (ADR 0017: "internal elevated read
-- scope ... unconditional across the whole database by design"). Filtering
-- results down to only what an authorized caller may see is the
-- persistence-layer cost-input materialization step's responsibility
-- (§7.4), not this canonical discovery query's.
CREATE FUNCTION worker_overlapping_sessions(
    p_user_id bigint, p_query_start timestamptz, p_query_end timestamptz, p_as_of timestamptz)
RETURNS TABLE(session_id bigint, leaf_work_id bigint, started_at timestamptz, finished_at timestamptz, effective_finished_at timestamptz) AS
$$
    SELECT s.id, s.leaf_work_id, s.started_at, s.finished_at, COALESCE(s.finished_at, p_as_of)
    FROM work_session s
    WHERE s.worked_by_user_id = p_user_id
      AND s.started_at < p_query_end
      AND (s.finished_at IS NULL OR s.finished_at > p_query_start);
$$ LANGUAGE sql STABLE;
