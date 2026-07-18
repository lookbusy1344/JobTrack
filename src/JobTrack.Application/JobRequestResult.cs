namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Result of <see cref="IRequestCommands.SubmitAsync" /> (ADR 0033).</summary>
public sealed record JobRequestResult
{
	/// <summary>The anchor <c>job_node</c> identifier created for this request.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The holding area this request was submitted into.</summary>
	public required RequestHoldingAreaId HoldingAreaId { get; init; }

	/// <summary>The requester who submitted this request.</summary>
	public required AppUserId RequesterUserId { get; init; }

	/// <summary>The employee who directly owns the anchor node; <see langword="null" /> if unassigned (the pickup pool).</summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The request's description.</summary>
	public required string Description { get; init; }

	/// <summary>The instant this request was submitted.</summary>
	public required Instant SubmittedAt { get; init; }

	/// <summary>The instant staff acknowledged this request (ADR 0034), or <see langword="null" /> if not yet acknowledged.</summary>
	public required Instant? AcknowledgedAt { get; init; }

	/// <summary>The request's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
