namespace JobTrack.Application;

using Abstractions;
using Domain.Costing;
using NodaTime;

/// <summary>
///     One node in the requester-safe, read-only projection of a request's subtree (ADR 0034, plan §7).
///     Deliberately narrow — ADR 0054 adds aggregate allocated duration, but no owner, rates,
///     individual sessions, schedules, or audit fields; see <see cref="IRequestCommands.GetDetailAsync" />.
/// </summary>
public sealed record RequesterSubtreeNodeResult
{
	/// <summary>The node's identifier.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The node's description.</summary>
	public required string Description { get; init; }

	/// <summary>The node's own public status, derived the same way as the request's overall status.</summary>
	public required RequesterStatus Status { get; init; }

	/// <summary>The node's parent within the subtree, or <see langword="null" /> for the request's own anchor node.</summary>
	public required JobNodeId? ParentId { get; init; }

	/// <summary>The instant this node's requester-visible state was last updated.</summary>
	public required Instant LastUpdatedAt { get; init; }

	/// <summary>
	///     The concurrency-adjusted work duration recorded for this node's subtree. This is an
	///     aggregate only: it exposes neither individual sessions nor monetary cost.
	/// </summary>
	public AllocatedDuration AllocatedDuration { get; init; } = AllocatedDuration.Zero;
}
