namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Concurrency;

/// <summary>
///     Result of <see cref="IWorkSessionQueryPort.GetConcurrentSessionsAsync" />: the subject job's own
///     sessions and, on other jobs, the sessions of those same workers that intersect them — both sides
///     already clipped to <c>asOf</c>, so <see cref="ConcurrentWorkCalculator" /> receives finite
///     intervals and performs no clipping of its own.
/// </summary>
internal sealed record ConcurrentWorkQueryResult
{
	/// <summary>The subject job's own sessions.</summary>
	public required EquatableArray<ConcurrentWorkSession> SubjectSessions { get; init; }

	/// <summary>The same workers' intersecting sessions on other jobs.</summary>
	public required EquatableArray<ConcurrentWorkSession> ConcurrentSessions { get; init; }

	/// <summary>Whether either side hit its <see cref="ConcurrentWorkLimits" /> cap, making the load a partial one.</summary>
	public required bool IsTruncated { get; init; }
}
