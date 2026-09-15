namespace JobTrack.Abstractions;

/// <summary>
///     The passkey (WebAuthn) credential policy applied at every JobTrack enrolment boundary
///     (ADR 0071). A memorable friendly name is required, unique per account under
///     ordinal-ignore-case comparison; an account holds a bounded number of credentials.
/// </summary>
public static class PasskeyPolicy
{
	/// <summary>The most passkeys one account may hold at once (ADR 0071 §3).</summary>
	public const int MaxPasskeysPerAccount = 10;

	/// <summary>The minimum accepted friendly-name length in Unicode code points.</summary>
	public const int MinimumNameLength = 1;

	/// <summary>The maximum accepted friendly-name length in Unicode code points.</summary>
	public const int MaximumNameLength = 100;

	/// <summary>The number of cryptographically random bytes in a WebAuthn user handle before base64url encoding.</summary>
	public const int UserHandleByteLength = 32;

	/// <summary>The base64url character length of a 32-byte user handle.</summary>
	public const int UserHandleEncodedLength = 43;

	/// <summary>The maximum WebAuthn credential identifier length accepted by CTAP/WebAuthn.</summary>
	public const int MaximumCredentialIdByteLength = 1023;

	/// <summary>The maximum persisted COSE public-key representation.</summary>
	public const int MaximumPublicKeyByteLength = 4096;

	/// <summary>The maximum canonical transport JSON length.</summary>
	public const int MaximumTransportsLength = 512;

	/// <summary>The maximum retained attestation object length.</summary>
	public const int MaximumAttestationObjectByteLength = 16384;

	/// <summary>The maximum retained client-data JSON length.</summary>
	public const int MaximumClientDataJsonByteLength = 4096;

	/// <summary>The maximum assertion or attestation request JSON body.</summary>
	public const int MaximumCredentialJsonByteLength = 32768;

	/// <summary>Returns whether <paramref name="name" /> is a non-blank friendly name within the length bounds.</summary>
	public static bool IsNameAcceptable(string? name)
	{
		if (string.IsNullOrWhiteSpace(name)) {
			return false;
		}

		var codePointCount = TextLength.CodePointCount(name);
		return codePointCount is >= MinimumNameLength and <= MaximumNameLength;
	}

	/// <summary>
	///     The invariant normalization used for per-account name uniqueness. Derived once and persisted
	///     so the database compares the stored value exactly, never via provider <c>lower</c>/<c>NOCASE</c>
	///     or locale (ADR 0071 §3).
	/// </summary>
	public static string Normalize(string name)
	{
		ArgumentNullException.ThrowIfNull(name);

		return name.Trim().ToUpperInvariant();
	}
}
