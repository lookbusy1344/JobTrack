namespace JobTrack.Application;

/// <summary>Updated account state after enrolling a passkey.</summary>
public sealed class AddPasskeyResult
{
	/// <summary>The new security stamp after the credential transition.</summary>
	public required string SecurityStamp { get; init; }

	/// <summary>The new concurrency stamp after the credential transition.</summary>
	public required string ConcurrencyStamp { get; init; }

	/// <summary>The opaque (base64url) identifier of the credential just stored.</summary>
	public required string CredentialId { get; init; }
}
