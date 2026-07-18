namespace JobTrack.Application.Ports;

/// <summary>Persistence port for authentication audit events.</summary>
public interface IAuthenticationAuditPort
{
	/// <inheritdoc cref="IAuthenticationAuditCommands.RecordAsync" />
	Task RecordAsync(RecordAuthenticationAuditEventRequest request, CancellationToken cancellationToken = default);
}
