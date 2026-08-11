namespace JobTrack.Application;

/// <summary>
///     Named page-size bounds for <see cref="IAuditQueries.SearchAuditEventsAsync" /> (fresh-eyes review
///     §2.3): an empty/absent request page size uses <see cref="DefaultPageSize" />; any requested size
///     is clamped down to <see cref="MaxPageSize" /> before it reaches persistence, so no search can ever
///     materialize more than <see cref="MaxPageSize" /> + 1 rows (the one extra probe row that reveals
///     whether a further page exists).
/// </summary>
public static class AuditSearchPaging
{
	/// <summary>The page size used when a request omits or supplies a non-positive page size.</summary>
	public const int DefaultPageSize = 50;

	/// <summary>The largest page size a request may ask for; larger requests are silently clamped down to this.</summary>
	public const int MaxPageSize = 200;
}
