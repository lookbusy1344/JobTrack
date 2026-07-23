namespace JobTrack.Application;

using Abstractions;
using Domain.Authorization;
using Ports;

/// <summary>
///     Implements audit history search (plan §7.3 step 11) by loading matching raw events through
///     <see cref="IAuditQueryPort" />, applying <see cref="AuditAccessPolicy" /> as a baseline gate, then
///     projecting each event: a rate/cost-bearing event's before/after payload is withheld from a
///     caller who lacks <see cref="CostAccessPolicy" /> visibility, independent of whether they may
///     search audit history at all (spec §16's rate/cost permission is layered on top of, not a
///     substitute for, the audit-search gate). Bounds every search to a page of at most
///     <see cref="AuditSearchPaging.MaxPageSize" /> events (fresh-eyes review §2.3): the port is always
///     asked for one more row than the page size, so the extra row's presence -- never a second count
///     query -- decides whether a continuation cursor is returned.
/// </summary>
internal sealed class AuditQueries : IAuditQueries
{
	private readonly IAuditQueryPort _port;

	/// <summary>Creates an <see cref="AuditQueries" /> over the given port.</summary>
	public AuditQueries(IAuditQueryPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
	}

	/// <inheritdoc />
	public Task<AuditEventSearchResult> SearchAuditEventsAsync(
		AuditEventSearchRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return SearchAuditEventsCoreAsync(request, cancellationToken);
	}

	private static AuditEventResult Project(AuditEventRecord record, bool canViewCosts)
	{
		var redact = record.IsSensitive && !canViewCosts;

		return new() {
			Id = record.Id,
			OccurredAt = record.OccurredAt,
			ActorId = record.ActorId,
			Operation = record.Operation,
			EntityType = record.EntityType,
			EntityId = record.EntityId,
			CorrelationId = record.CorrelationId,
			Reason = record.Reason,
			BeforeData = redact ? null : record.BeforeData,
			AfterData = redact ? null : record.AfterData,
			IsRedacted = redact,
		};
	}

	private static int ResolvePageSize(int? requested) =>
		requested is > 0 ? Math.Min(requested.Value, AuditSearchPaging.MaxPageSize) : AuditSearchPaging.DefaultPageSize;

	private Task<AuditEventSearchResult> SearchAuditEventsCoreAsync(
		AuditEventSearchRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"audit.search-events", request.Context, null,
			async () => {
				AuditEventSearchCursor? before = null;
				if (request.Cursor is not null) {
					if (!AuditEventCursorCodec.TryDecode(request.Cursor, out var decoded)) {
						throw new ArgumentException("The audit search cursor is malformed.", nameof(request));
					}

					before = decoded;
				}

				var actorRoles = await _port.GetActorRolesAsync(request.Context.Actor, cancellationToken).ConfigureAwait(false);

				if (!AuditAccessPolicy.CanSearch(actorRoles)) {
					throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not search audit history.");
				}

				var pageSize = ResolvePageSize(request.PageSize);
				var result = await _port.SearchAuditEventsAsync(request.Filter, before, pageSize + 1, cancellationToken)
					.ConfigureAwait(false);

				// Audit search spans events across many unrelated nodes, not one queried node, so
				// ADR 0040's ownership carve-out (scoped to a single node's ancestry) doesn't apply here.
				var canViewCosts = CostAccessPolicy.CanView(actorRoles, false);

				var hasMore = result.Events.Count > pageSize;
				var page = hasMore ? result.Events.Take(pageSize).ToArray() : [.. result.Events];
				var continuationCursor = hasMore
					? AuditEventCursorCodec.Encode(new() { OccurredAt = page[^1].OccurredAt, Id = page[^1].Id })
					: null;

				return new AuditEventSearchResult {
					Events = [.. page.Select(record => Project(record, canViewCosts))],
					ContinuationCursor = continuationCursor,
				};
			});
}
