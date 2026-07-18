namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.ImportSubtreeAsync" />: atomically creates a whole batch of new
///     job nodes — a subtree of any shape, plus prerequisite edges between them — in one transaction.
///     Either every node and edge in <see cref="Nodes" /> is created, or none is; a bulk-authoring tool
///     for small trees is the motivating use case (e.g. <c>JobTrack.AdminCli</c>'s <c>import-tree</c>
///     command), where a partial failure part-way through would otherwise leave an operator having to
///     work out by hand what to clean up.
/// </summary>
public sealed record ImportSubtreeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>
	///     The existing node every root-level entry in <see cref="Nodes" /> (one with a
	///     <see langword="null" /> <see cref="ImportSubtreeNodeSpec.ParentLocalId" />) attaches under.
	/// </summary>
	public required JobNodeId ParentId { get; init; }

	/// <summary>
	///     The new subtree's nodes, in any order — <see cref="IJobCommands.ImportSubtreeAsync" />
	///     determines a valid parents-before-children creation order itself.
	/// </summary>
	public required EquatableArray<ImportSubtreeNodeSpec> Nodes { get; init; }
}
