namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the append-only <c>job_request_note</c> table (ADR 0034): one note on a
///     request's notes thread, written by either staff or the requester, rooted at the request's anchor
///     <c>job_node</c>.
/// </summary>
internal sealed class JobRequestNoteEntity
{
	public required JobRequestNoteId Id { get; set; }

	public required JobNodeId JobNodeId { get; set; }

	public required AppUserId AuthorUserId { get; set; }

	public required string Content { get; set; }

	public bool IsVisibleToRequester { get; set; }

	public Instant CreatedAt { get; set; }
}
