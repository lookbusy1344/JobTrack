namespace JobTrack.Application.Ports;

/// <summary>Persistence port for authentication audit events.</summary>
internal interface IAuthenticationAuditPort
{
	/// <inheritdoc cref="IAuthenticationAuditCommands.RecordAsync" />
	Task RecordAsync(RecordAuthenticationAuditEventRequest request, CancellationToken cancellationToken = default);
}
