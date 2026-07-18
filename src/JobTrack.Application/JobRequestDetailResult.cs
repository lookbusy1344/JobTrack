namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One request's requester-safe detail projection (ADR 0034, plan §7/§8 <c>/Requests/{id}</c>):
///     status, the read-only subtree, and the notes visible to the calling actor. A requester caller sees
///     only requester-visible notes; a staff/admin caller sees every note.
/// </summary>
public sealed record JobRequestDetailResult
{
	/// <summary>The request's anchor <c>job_node</c> identifier.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The anchor node's description.</summary>
	public required string Description { get; init; }

	/// <summary>The request's public status, derived from the whole subtree (ADR 0034).</summary>
	public required RequesterStatus Status { get; init; }

	/// <summary>The instant this request was submitted.</summary>
	public required Instant SubmittedAt { get; init; }

	/// <summary>The instant staff acknowledged this request, or <see langword="null" /> if not yet acknowledged.</summary>
	public required Instant? AcknowledgedAt { get; init; }

	/// <summary>The request's optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>The requester-safe subtree rooted at the anchor node, including descendants created by decomposition.</summary>
	public required EquatableArray<RequesterSubtreeNodeResult> Subtree { get; init; }

	/// <summary>The notes visible to the calling actor, oldest first.</summary>
	public required EquatableArray<JobRequestNoteResult> Notes { get; init; }
}
