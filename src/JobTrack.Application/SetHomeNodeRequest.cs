namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommands.SetHomeNodeAsync" />.</summary>
public sealed record SetHomeNodeRequest
{
	/// <summary>
	///     The acting user and correlation identifier. Always the employee whose own home
	///     node is being set — there is no separate target, this is self-service only.
	/// </summary>
	public required CommandContext Context { get; init; }

	/// <summary>
	///     The node to land on after login, or <see langword="null" /> to reset to the tree
	///     root.
	/// </summary>
	public JobNodeId? NodeId { get; init; }
}
