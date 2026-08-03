namespace JobTrack.Domain.Concurrency;

using Abstractions;
using NodaTime;

/// <summary>
///     One worker's total concurrent time between the subject job and one other job: the aggregate
///     <see cref="ConcurrentWorkCalculator" /> produces per (worker, other node) pair. This is raw
///     wall-clock intersection of recorded sessions, not the cost engine's allocated share — working-time
///     eligibility and rates never enter into it, so a row says only "this worker was clocked on to both
///     jobs for this long".
/// </summary>
/// <param name="WorkedByUserId">The worker who was clocked on to both jobs.</param>
/// <param name="NodeId">The other job node whose sessions intersect the subject's.</param>
/// <param name="TotalOverlap">The summed duration of every intersection between the two jobs' sessions.</param>
/// <param name="OverlapCount">How many session pairs intersected, so a long total from many short collisions reads differently from one long one.</param>
/// <param name="FirstOverlapStart">The earliest instant the two jobs were worked at once.</param>
/// <param name="LastOverlapEnd">The latest instant the two jobs were worked at once.</param>
public sealed record ConcurrentWorkOverlap(
	AppUserId WorkedByUserId,
	JobNodeId NodeId,
	Duration TotalOverlap,
	int OverlapCount,
	Instant FirstOverlapStart,
	Instant LastOverlapEnd);
