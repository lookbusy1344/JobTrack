namespace JobTrack.Application;

/// <summary>Input to <see cref="IAuditQueries.SearchAuditEventsAsync" />.</summary>
public sealed record AuditEventSearchRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>Optional narrowing criteria; an absent filter matches every event.</summary>
	public required AuditEventSearchFilter Filter { get; init; }
}
