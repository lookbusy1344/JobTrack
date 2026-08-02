namespace JobTrack.Abstractions;

using System.Collections.Frozen;

/// <summary>
///     A local, deterministic blocklist of common/breached and context-specific passwords
///     (remediation plan §2.1, ADR 0056). Comparison is an exact, case-insensitive match against the
///     supplied value -- a floor against the most-guessed and most-reused values, not a substitute
///     for <see cref="PasswordPolicy" />'s length requirement. Fully local: no plaintext password is
///     ever sent anywhere to perform this check.
/// </summary>
public static class PasswordBlocklist
{
	/// <summary>Always-blocked regardless of case: the product's own name is a predictable guess.</summary>
	private const string ProductName = "JobTrack";

	// A sample of values that satisfy PasswordPolicy.MinimumLength on their own but are still
	// well-known weak choices: top breached-password-list entries long enough to pass a naive length
	// check, common keyboard walks/padding, and "correcthorsebatterystaple" -- the XKCD passphrase,
	// now blocklisted by real password managers and NIST guidance precisely because its fame makes it
	// a common guess. This is necessarily a floor, not exhaustive breached-password coverage; see the
	// ADR for why a fully offline, deterministic list was chosen over a live compromised-password
	// lookup service.
	private static readonly FrozenSet<string> CommonPasswords = new[] {
		"passwordpassword", "password1234567", "letmeinletmein12", "iloveyouiloveyou", "qwertyuiopqwerty", "1234567890123456", "aaaaaaaaaaaaaaaa",
		"trustno1trustno1", "welcometothejungle", "dragondragondragon", "supermansuperman", "changeitchangeit", "adminadminadmin",
		"correcthorsebatterystaple", "thequickbrownfox", "mynameismynameis",
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	///     Returns whether <paramref name="password" /> is blocked: it equals the product name, the
	///     given <paramref name="username" />, or a listed common/breached value (all case-insensitive).
	/// </summary>
	public static bool Contains(string? password, string? username = null)
	{
		if (string.IsNullOrEmpty(password)) {
			return false;
		}

		if (password.Equals(ProductName, StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		if (!string.IsNullOrEmpty(username) && password.Equals(username, StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		return CommonPasswords.Contains(password);
	}
}
