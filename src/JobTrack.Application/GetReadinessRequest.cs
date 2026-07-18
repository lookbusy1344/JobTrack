namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetReadinessAsync" />. Unlike <see cref="GetEmployeeProfileRequest" />,
///     this carries no ownership-based authorization gate — spec §7.3 lists viewing job data as an
///     unqualified baseline capability for every role, unlike employee-account data.
/// </summary>
public sealed record GetReadinessRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node whose readiness is requested.</summary>
	public required JobNodeId NodeId { get; init; }
}
