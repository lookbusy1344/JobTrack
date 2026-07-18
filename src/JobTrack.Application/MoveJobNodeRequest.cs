namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IJobCommands.MoveAsync" />.</summary>
public sealed record MoveJobNodeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node being moved.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The node's new parent.</summary>
	public required JobNodeId NewParentId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
