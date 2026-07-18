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
///     substitute for, the audit-search gate).
/// </summary>
public sealed class AuditQueries : IAuditQueries
{
	private readonly IAuditQueryPort _port;

	/// <summary>Creates an <see cref="AuditQueries" /> over the given port.</summary>
	public AuditQueries(IAuditQueryPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<AuditEventResult>> SearchAuditEventsAsync(
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

	private Task<IReadOnlyList<AuditEventResult>> SearchAuditEventsCoreAsync(
		AuditEventSearchRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"audit.search-events", request.Context, null,
			async () => {
				var actorRoles = await _port.GetActorRolesAsync(request.Context.Actor, cancellationToken).ConfigureAwait(false);

				if (!AuditAccessPolicy.CanSearch(actorRoles)) {
					throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not search audit history.");
				}

				var result = await _port.SearchAuditEventsAsync(request.Context.Actor, request.Filter, cancellationToken).ConfigureAwait(false);

				// Audit search spans events across many unrelated nodes, not one queried node, so
				// ADR 0040's ownership carve-out (scoped to a single node's ancestry) doesn't apply here.
				var canViewCosts = CostAccessPolicy.CanView(actorRoles, false);

				return (IReadOnlyList<AuditEventResult>)[.. result.Events.Select(record => Project(record, canViewCosts))];
			});
}
