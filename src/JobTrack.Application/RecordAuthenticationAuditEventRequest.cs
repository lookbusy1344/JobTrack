namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for recording an authentication event in the audit trail.</summary>
public sealed class RecordAuthenticationAuditEventRequest
{
	/// <summary>The known account actor. Null only for redacted unknown-user authentication failures.</summary>
	public AppUserId? ActorUserId { get; init; }

	/// <summary>The known <c>identity_user</c> row. Null only for redacted unknown-user authentication failures.</summary>
	public long? IdentityUserId { get; init; }

	/// <summary>The authentication event category.</summary>
	public required AuthenticationAuditEventKind Kind { get; init; }

	/// <summary>Correlates this event with request logs.</summary>
	public required Guid CorrelationId { get; init; }
}
