namespace JobTrack.Application;

using Abstractions;
using Ports;

/// <summary>Application command surface for authentication audit events.</summary>
internal sealed class AuthenticationAuditCommands : IAuthenticationAuditCommands
{
	private readonly IAuthenticationAuditPort port;

	/// <summary>Creates the command surface over the persistence port.</summary>
	public AuthenticationAuditCommands(IAuthenticationAuditPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		this.port = port;
	}

	/// <inheritdoc />
	public Task RecordAsync(RecordAuthenticationAuditEventRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		var hasKnownActor = request.ActorUserId is AppUserId actor && !actor.IsUnspecified;
		var hasKnownIdentityUser = request.IdentityUserId is not null;
		if (hasKnownActor != hasKnownIdentityUser) {
			throw new ArgumentException("Known authentication audit events must include both actor and identity user identifiers.", nameof(request));
		}

		if (!hasKnownActor && request.Kind is not AuthenticationAuditEventKind.LoginFailed) {
			throw new ArgumentException("Only failed password login events may be recorded without a known actor.", nameof(request));
		}

		return request.ActorUserId.HasValue
			? JobTrackOperation.TraceAsync(
				"authentication-audit.record",
				new() { Actor = request.ActorUserId.Value, CorrelationId = request.CorrelationId },
				null,
				() => port.RecordAsync(request, cancellationToken))
			: JobTrackOperation.TraceAsync(
				"authentication-audit.record",
				() => port.RecordAsync(request, cancellationToken));
	}
}
