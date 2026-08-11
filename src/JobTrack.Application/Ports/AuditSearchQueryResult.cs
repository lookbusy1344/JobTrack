namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>Result of <see cref="IAuditQueryPort.SearchAuditEventsAsync" />: the matching raw events, bounded by its <c>limit</c> parameter.</summary>
internal sealed record AuditSearchQueryResult
{
	/// <summary>The matching audit events, most recent first, at most the requested limit.</summary>
	public required EquatableArray<AuditEventRecord> Events { get; init; }
}
