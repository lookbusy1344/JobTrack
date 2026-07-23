namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IAuditQueries.SearchAuditEventsAsync" /> (plan
///     §7.3 step 11). Materializes at most one page's worth of matching raw events, unconditionally of
///     the actor's permissions -- <see cref="AuditQueries" /> applies
///     <see cref="Domain.Authorization.AuditAccessPolicy" /> and the per-event sensitive-field
///     projection itself, the same reload-then-decide shape as the other read-only ports. The keyset
///     bound, ordering, and limit all execute in SQL (fresh-eyes review §2.3) -- no provider may load a
///     full result set and then filter/page it in memory.
/// </summary>
internal interface IAuditQueryPort
{
	/// <summary>Loads the actor's current roles for an authorization pre-check before audit search materialization.</summary>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default);

	/// <summary>
	///     Searches audit history ordered <c>(OccurredAt DESC, Id DESC)</c>, restricted to rows strictly
	///     after <paramref name="before" /> in that ordering (i.e. the page immediately following it), and
	///     bounded to at most <paramref name="limit" /> rows.
	/// </summary>
	/// <param name="filter">Narrowing criteria; an absent member matches every event.</param>
	/// <param name="before">
	///     The previous page's last row, or <see langword="null" /> to start from the most recent event.
	/// </param>
	/// <param name="limit">The maximum number of rows to materialize.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	Task<AuditSearchQueryResult> SearchAuditEventsAsync(
		AuditEventSearchFilter filter, AuditEventSearchCursor? before, int limit, CancellationToken cancellationToken = default);
}
