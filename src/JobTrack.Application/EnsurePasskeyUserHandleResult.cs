namespace JobTrack.Application;

/// <summary>The account's stable WebAuthn user handle, base64url-encoded.</summary>
public sealed class EnsurePasskeyUserHandleResult
{
	/// <summary>The base64url user handle, either freshly generated or the account's existing one.</summary>
	public required string UserHandle { get; init; }
}
