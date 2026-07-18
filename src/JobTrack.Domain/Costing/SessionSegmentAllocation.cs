namespace JobTrack.Domain.Costing;

using Abstractions;
using Intervals;

/// <summary>
///     One session's exact allocated share of one constant-membership segment (spec §10.3 steps 9-10):
///     the output of <see cref="CostSegmentPartitioner" />, before any rate is resolved or applied.
/// </summary>
public sealed record SessionSegmentAllocation(WorkInterval Segment, WorkSessionId SessionId, JobNodeId NodeId, AllocatedShare Share);
