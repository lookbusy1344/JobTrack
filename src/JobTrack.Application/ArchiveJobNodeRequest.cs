namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IJobCommands.ArchiveAsync" />.</summary>
public sealed record ArchiveJobNodeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node being archived.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
