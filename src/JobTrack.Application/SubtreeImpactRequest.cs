namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetSubtreeImpactAsync" />: the read-only manifest of everything a
///     <see cref="IJobCommands.DeleteSubtreeAsync" /> rooted here would destroy (ADR 0061).
/// </summary>
public sealed record SubtreeImpactRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node whose subtree — itself included — is being measured.</summary>
	public required JobNodeId RootId { get; init; }
}
