namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.DeleteAsync" />. Deletion always rejects a node with children, a
///     prerequisite edge, or the permanent root — those never cascade, regardless of role (ADR 0036). A
///     leaf's own <c>LeafWork</c> is deletable along with it when unused (no <c>WorkSession</c> rows);
///     when it has real session history, deletion additionally requires the
///     <see
///         cref="EmployeeRole.Administrator" />
///     role and a non-empty <see cref="Reason" /> (ADR 0036) — the
///     spec's default of never physically deleting cost-relevant history, overridable only by an
///     administrator with an explicit reason.
/// </summary>
public sealed record DeleteJobNodeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node being deleted.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     Why the node is being deleted. Ignored unless the node's <c>LeafWork</c> has one or more
	///     <c>WorkSession</c> rows, in which case it is required (ADR 0036) and recorded as the audit
	///     event's reason.
	/// </summary>
	public string? Reason { get; init; }
}
