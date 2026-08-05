namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.DeleteSubtreeAsync" /> (ADR 0061): recursively and permanently
///     destroys <see cref="RootId" /> and every descendant, including their <c>leaf_work</c>,
///     <c>work_session</c>, rate overrides, requests, and every prerequisite edge touching the subtree.
///     Requires <see cref="EmployeeRole.Administrator" />. The permanent root is never deletable, and a
///     subtree with a request holding area anchored inside it is refused.
/// </summary>
public sealed record DeleteSubtreeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The subtree root; it is deleted along with every descendant.</summary>
	public required JobNodeId RootId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version of <see cref="RootId" />.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     Why the subtree is being destroyed. Always required and non-empty — unlike single-node
	///     <see cref="DeleteJobNodeRequest.Reason" />, which ADR 0036 requires only for a worked leaf.
	///     Recorded as the audit event's reason, which outlives every row it describes.
	/// </summary>
	public required string Reason { get; init; }
}
