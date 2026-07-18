namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IRequestCommands.SubmitAsync" /> (ADR 0033). Deliberately narrow and
///     allow-listed: the caller supplies only what a requester may set. Parent, owner, kind, priority,
///     posted-by, and timestamps are never caller-supplied — they come from the holding area's
///     configuration and server-side defaults, so a request can never be mass-assigned into an
///     arbitrary parent, owner, or node kind.
/// </summary>
public sealed record SubmitJobRequestRequest
{
	/// <summary>The acting user and correlation identifier. <see cref="CommandContext.Actor" /> becomes the request's requester.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The holding area this request is submitted into.</summary>
	public required RequestHoldingAreaId HoldingAreaId { get; init; }

	/// <summary>The request's description.</summary>
	public required string Description { get; init; }

	/// <summary>Free-form supplementary detail/context supplied by the requester.</summary>
	public string? WriteUp { get; init; }

	/// <summary>An optional requester-supplied tracking label.</summary>
	public string? RequesterReference { get; init; }
}
