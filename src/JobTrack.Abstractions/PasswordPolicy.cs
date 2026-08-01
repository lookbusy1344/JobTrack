namespace JobTrack.Abstractions;

/// <summary>
///     The password length policy required by every JobTrack credential-setting boundary
///     (remediation plan §2.1, ADR 0056). NIST SP 800-63B's password-verifier requirements call for
///     at least 15 characters for a password used as a single factor (MFA is optional here, so every
///     account must be treated that way), no character-class composition rule, and acceptance of at
///     least 64 characters -- composition rules push users toward predictable substitutions instead
///     of real entropy, so this is length-only.
/// </summary>
public static class PasswordPolicy
{
	/// <summary>The minimum accepted number of Unicode code points (NIST SP 800-63B, single-factor password).</summary>
	public const int MinimumLength = 15;

	/// <summary>
	///     The maximum accepted number of Unicode code points. Comfortably above NIST's 64-character
	///     floor while bounding hashing cost and storage.
	/// </summary>
	public const int MaximumLength = 128;

	/// <summary>Returns whether <paramref name="password" />'s length satisfies the shared policy.</summary>
	public static bool IsSatisfiedBy(string? password)
	{
		if (password is null) {
			return false;
		}

		// Unicode code points, not UTF-16 code units -- a password containing surrogate-pair
		// characters (e.g. emoji) must count each as one, matching "15 Unicode code points" rather
		// than counting each surrogate half separately.
		var codePointCount = password.EnumerateRunes().Count();
		return codePointCount is >= MinimumLength and <= MaximumLength;
	}
}
