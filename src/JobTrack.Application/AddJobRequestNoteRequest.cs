namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IRequestCommands.AddNoteAsync" /> (ADR 0034). The port authorizes and brands
///     this note as staff- or requester-authored by reloading the actor's roles and relationship to the
///     request itself, not from a caller-supplied flag. <see cref="VisibleToRequester" /> is honored only
///     when the actor is writing as staff (<see cref="Domain.Authorization.JobNodeAccessPolicy.CanManage" />);
///     a requester-authored note always ends up visible to the requester regardless of this value, since
///     a requester cannot post a private note to themselves.
/// </summary>
public sealed record AddJobRequestNoteRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The request's anchor node the note is attached to.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The note's text. Must not be blank.</summary>
	public required string Content { get; init; }

	/// <summary>Whether a staff-authored note is visible to the requester. Ignored for a requester-authored note.</summary>
	public required bool VisibleToRequester { get; init; }
}
