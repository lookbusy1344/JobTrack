namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One node in <see cref="ImportSubtreeRequest.Nodes" />. <see cref="LocalId" />/<see cref="ParentLocalId" />/
///     <see cref="PrerequisiteLocalIds" /> are caller-chosen identifiers scoped to one request only —
///     never <c>job_node</c> identifiers themselves — letting a caller describe a whole new subtree,
///     parents and prerequisite edges included, before any of it has a real database identity.
/// </summary>
public sealed record ImportSubtreeNodeSpec
{
	/// <summary>This node's request-local identifier, unique within <see cref="ImportSubtreeRequest.Nodes" />.</summary>
	public required long LocalId { get; init; }

	/// <summary>
	///     The request-local identifier of this node's parent within the batch, or
	///     <see langword="null" /> to attach directly under <see cref="ImportSubtreeRequest.ParentId" />.
	/// </summary>
	public long? ParentLocalId { get; init; }

	/// <summary>The new node's description.</summary>
	public required string Description { get; init; }

	/// <summary>Free-form supplementary detail.</summary>
	public string? WriteUp { get; init; }

	/// <summary>
	///     The employee who directly owns the new node and, for authorization, its subtree; <see langword="null" /> to leave it unassigned (the pickup
	///     pool).
	/// </summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The new node's priority.</summary>
	public required Priority Priority { get; init; }

	/// <summary>The estimated effort, in hours.</summary>
	public decimal? ExpectedDurationHours { get; init; }

	/// <summary>The estimated cost.</summary>
	public Money? ExpectedCost { get; init; }

	/// <summary>The instant this node's work is needed to start.</summary>
	public Instant? NeededStart { get; init; }

	/// <summary>The instant this node's work is needed to finish.</summary>
	public Instant? NeededFinish { get; init; }

	/// <summary>
	///     Request-local identifiers of nodes in the same batch that must succeed before this
	///     one is ready (spec §6). Every id must resolve to another node in the same
	///     <see cref="ImportSubtreeRequest.Nodes" /> batch.
	/// </summary>
	public EquatableArray<long> PrerequisiteLocalIds { get; init; } = [];

	/// <summary>
	///     Work already performed against this node, recorded in the same transaction that creates it,
	///     or <see langword="null" /> (the default) for a node imported with no work yet. Only a leaf may
	///     carry this: a node that is another batch node's <see cref="ParentLocalId" /> cannot hold
	///     <c>LeafWork</c>, and the import rejects the batch rather than silently dropping the work.
	/// </summary>
	public ImportSubtreeLeafWorkSpec? LeafWork { get; init; }
}
