namespace JobTrack.Application;

/// <summary>Updated account state after a self-service password change (remediation plan §2.2).</summary>
public sealed class ChangeOwnPasswordResult
{
	/// <summary>The new security stamp after the credential transition.</summary>
	public required string SecurityStamp { get; init; }

	/// <summary>The new concurrency stamp after the credential transition.</summary>
	public required string ConcurrencyStamp { get; init; }
}
