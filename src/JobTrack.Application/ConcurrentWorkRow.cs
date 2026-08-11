namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One row of <see cref="ConcurrentWorkResult" />: one worker, one other job they were clocked on
///     to while the subject job's own sessions were running, and how much of that time the two jobs
///     shared. <see cref="Node" /> carries the other job's current summary so a caller can render it
///     with the same description/kind/achievement/owner treatment as any other job list.
/// </summary>
public sealed record ConcurrentWorkRow
{
	/// <summary>The employee who was clocked on to both jobs.</summary>
	public required AppUserId WorkedByUserId { get; init; }

	/// <summary>The other job node's current summary.</summary>
	public required JobNodeSummaryResult Node { get; init; }

	/// <summary>
	///     The summed wall-clock time the two jobs' sessions overlapped. Raw recorded overlap, never the
	///     cost engine's allocated share — working-time eligibility and rates play no part.
	/// </summary>
	public required Duration TotalOverlap { get; init; }

	/// <summary>How many session pairs overlapped, so many brief collisions read differently from one long one.</summary>
	public required int OverlapCount { get; init; }

	/// <summary>The earliest instant both jobs were being worked at once.</summary>
	public required Instant FirstOverlapStart { get; init; }

	/// <summary>The latest instant both jobs were being worked at once.</summary>
	public required Instant LastOverlapEnd { get; init; }
}
