namespace JobTrack.Application;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Adds a caller-selected prerequisite edge batch atomically. Every edge is authorized,
///     validated, inserted, and audited under <see cref="Context" /> in one provider transaction;
///     one rejected edge rolls back the entire selection.
/// </summary>
public sealed record AddPrerequisitesRequest
{
	/// <summary>The acting user and correlation identifier shared by the complete batch.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The non-empty set of directed prerequisite edges to add.</summary>
	public required EquatableArray<PrerequisiteEdge> Edges { get; init; }
}
