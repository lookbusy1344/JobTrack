namespace JobTrack.Domain.Costing;

using Abstractions;
using Intervals;

/// <summary>
///     One work session's already-eligible-clipped interval, ready for boundary partitioning (spec
///     §10.1/§10.3): the caller has already bounded a null <c>FinishedAt</c> by <c>asOf</c> and
///     resolved which worker's other sessions to include, per the database-wide concurrency discovery
///     of spec §10.2.2. <see cref="NodeId" /> is the session's <c>LeafWork</c> node, carried through so a
///     later rate-resolution step can apply <see cref="Rates.RateResolver" /> per allocation.
/// </summary>
public sealed record CostableSession(WorkSessionId SessionId, JobNodeId NodeId, WorkInterval Interval);
