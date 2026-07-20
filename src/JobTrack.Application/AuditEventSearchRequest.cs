namespace JobTrack.Application;

/// <summary>Input to <see cref="IAuditQueries.SearchAuditEventsAsync" />.</summary>
public sealed record AuditEventSearchRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>Optional narrowing criteria; an absent filter matches every event.</summary>
	public required AuditEventSearchFilter Filter { get; init; }

	/// <summary>
	///     The previous page's <see cref="AuditEventSearchResult.ContinuationCursor" />, or
	///     <see langword="null" /> to fetch the first page. Round-tripped opaquely -- never construct or
	///     parse this token.
	/// </summary>
	public string? Cursor { get; init; }

	/// <summary>
	///     The requested page size. A <see langword="null" /> or non-positive value uses
	///     <see cref="AuditSearchPaging.DefaultPageSize" />; any value is clamped down to
	///     <see cref="AuditSearchPaging.MaxPageSize" />.
	/// </summary>
	public int? PageSize { get; init; }
}
