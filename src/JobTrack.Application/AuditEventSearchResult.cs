namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     One bounded page of <see cref="IAuditQueries.SearchAuditEventsAsync" /> (fresh-eyes review
///     §2.3): at most the request's page size (or <see cref="AuditSearchPaging.DefaultPageSize" />) of
///     <see cref="AuditEventResult" />, most recent first, plus an opaque
///     <see cref="ContinuationCursor" /> to fetch the next page when one exists.
/// </summary>
public sealed record AuditEventSearchResult
{
	/// <summary>This page's matching events, most recent first.</summary>
	public required EquatableArray<AuditEventResult> Events { get; init; }

	/// <summary>
	///     An opaque token to pass back as the next request's <see cref="AuditEventSearchRequest.Cursor" />,
	///     or <see langword="null" /> when this page is the last one for the current filter.
	/// </summary>
	public required string? ContinuationCursor { get; init; }
}
