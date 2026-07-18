namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.AddPrerequisiteAsync" /> (spec §6): <see cref="RequiredJobId" />
///     must reach derived <c>Achievement.Success</c> before <see cref="DependentJobId" /> is ready.
/// </summary>
public sealed record AddPrerequisiteRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The job that must succeed first.</summary>
	public required JobNodeId RequiredJobId { get; init; }

	/// <summary>The job gated on <see cref="RequiredJobId" />.</summary>
	public required JobNodeId DependentJobId { get; init; }
}
