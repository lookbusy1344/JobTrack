namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>One note on a request's notes thread (ADR 0034). Append-only; see <see cref="IRequestCommands.AddNoteAsync" />.</summary>
public sealed record JobRequestNoteResult
{
	/// <summary>The note's identifier.</summary>
	public required JobRequestNoteId Id { get; init; }

	/// <summary>The note's author.</summary>
	public required AppUserId AuthorUserId { get; init; }

	/// <summary>The note's text.</summary>
	public required string Content { get; init; }

	/// <summary>Whether this note is visible to the requester. Always <see langword="true" /> for a requester-authored note.</summary>
	public required bool VisibleToRequester { get; init; }

	/// <summary>The instant this note was written.</summary>
	public required Instant CreatedAt { get; init; }
}
