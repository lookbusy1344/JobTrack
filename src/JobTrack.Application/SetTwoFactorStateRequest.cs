namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for self-service two-factor enablement or disablement.</summary>
public sealed class SetTwoFactorStateRequest
{
	/// <summary>The signed-in account changing its own two-factor state.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The signed-in account's <c>identity_user</c> row.</summary>
	public long IdentityUserId { get; init; }

	/// <summary>Whether two-factor authentication should be enabled.</summary>
	public bool Enabled { get; init; }

	/// <summary>Correlates the state change with audit events and logs.</summary>
	public required Guid CorrelationId { get; init; }
}
