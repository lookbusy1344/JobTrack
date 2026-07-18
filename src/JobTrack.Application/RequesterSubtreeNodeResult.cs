namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One node in the requester-safe, read-only projection of a request's subtree (ADR 0034, plan §7).
///     Deliberately narrow — no owner, rates, sessions, schedules, or audit fields; see
///     <see cref="IRequestCommands.GetDetailAsync" />.
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
}
