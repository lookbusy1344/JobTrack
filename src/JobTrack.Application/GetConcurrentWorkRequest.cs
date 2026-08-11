namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IJobQueries.GetConcurrentWorkAsync" />. Carries no ownership-based
///     authorization gate (see <see cref="GetJobNodeRequest" />) beyond the baseline-employee admission
///     every general job/work read shares: which jobs a worker was clocked on to is work-session data,
///     open to every employee role (ADR 0041).
/// </summary>
public sealed record GetConcurrentWorkRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The job node whose sessions every other job's sessions are intersected against.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>
	///     The instant an unfinished session is bounded by, exactly as cost calculation bounds one
	///     (spec §10.1). Defaults to now, so a session still running counts up to the moment of the
	///     query rather than being skipped or treated as unbounded.
	/// </summary>
	public Instant? AsOf { get; init; }
}
