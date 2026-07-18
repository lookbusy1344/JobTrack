namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IJobCommands.RemovePrerequisiteAsync" />.</summary>
public sealed record RemovePrerequisiteRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The required job of the edge being removed.</summary>
	public required JobNodeId RequiredJobId { get; init; }

	/// <summary>The dependent job of the edge being removed.</summary>
	public required JobNodeId DependentJobId { get; init; }
}
