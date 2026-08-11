namespace JobTrack.Domain.Costing;

using Abstractions;
using Intervals;
using Rates;

/// <summary>
///     The canonical explanation of one session's allocation within a constant-membership,
///     constant-rate cost segment. Trace entries are derived and disposable, never authoritative.
/// </summary>
public sealed record CostSegmentTrace(
	WorkInterval Segment,
	bool IsWorkingTime,
	EquatableArray<WorkSessionId> ActiveSessionIds,
	WorkSessionId SessionId,
	JobNodeId NodeId,
	AllocatedShare AllocatedDuration,
	ResolvedRate ResolvedRate,
	Money UnroundedContribution);
