namespace JobTrack.Application;

/// <summary>Records authentication and credential self-service events in the append-only audit trail.</summary>
public interface IAuthenticationAuditCommands
{
	/// <summary>Records one authentication event without storing submitted credentials or usernames.</summary>
	Task RecordAsync(RecordAuthenticationAuditEventRequest request, CancellationToken cancellationToken = default);
}
