namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     A position in <see cref="IAuditQueries.SearchAuditEventsAsync" />'s <c>(OccurredAt DESC, Id DESC)</c>
///     ordering (fresh-eyes review §2.3): the last row kept on a page, used both as the exclusive lower
///     bound for the next page's keyset predicate and as the decoded form of an
///     <see cref="AuditEventSearchResult.ContinuationCursor" /> opaque token.
/// </summary>
internal sealed record AuditEventSearchCursor
{
	/// <summary>The boundary row's <see cref="AuditEventResult.OccurredAt" />.</summary>
	public required Instant OccurredAt { get; init; }

	/// <summary>The boundary row's <see cref="AuditEventResult.Id" />, tie-breaking equal <see cref="OccurredAt" /> values.</summary>
	public required AuditEventId Id { get; init; }
}
