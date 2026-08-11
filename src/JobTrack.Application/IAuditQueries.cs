namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Audit history search (plan §7.3 step 11: append-only audit search with permission-sensitive
///     projections; docs/api/jobtrack-client-design.md). Gated by
///     <see
///         cref="Domain.Authorization.AuditAccessPolicy" />
///     — unlike ordinary job/schedule visibility, the
///     audit log itself is never an unqualified baseline capability (spec §7.3).
/// </summary>
public interface IAuditQueries
{
	/// <summary>
	///     Searches audit history, returning matching events most recent first. A rate/cost-bearing
	///     event's before/after payload is withheld (<see cref="AuditEventResult.IsRedacted" />) from a
	///     caller who lacks <see cref="Domain.Authorization.CostAccessPolicy" /> visibility, even though
	///     the event itself remains visible (spec §16). Results are bounded to at most
	///     <see cref="AuditSearchPaging.MaxPageSize" /> events per call, with
	///     <see cref="AuditEventSearchResult.ContinuationCursor" /> set when more remain (fresh-eyes
	///     review §2.3).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold audit-search permission (see <see cref="Domain.Authorization.AuditAccessPolicy" />).
	/// </exception>
	/// <exception cref="ArgumentException">
	///     <paramref name="request" />'s <see cref="AuditEventSearchRequest.Cursor" /> is malformed.
	/// </exception>
	Task<AuditEventSearchResult> SearchAuditEventsAsync(
		AuditEventSearchRequest request, CancellationToken cancellationToken = default);
}
