namespace JobTrack.Abstractions;

/// <summary>
///     The password complexity required by every JobTrack credential-changing boundary: at least
///     <see cref="MinimumLength" /> characters, including one letter and one digit.
/// </summary>
public static class PasswordPolicy
{
	/// <summary>The minimum accepted password length.</summary>
	public const int MinimumLength = 6;

	/// <summary>Returns whether <paramref name="password" /> satisfies the complete shared policy.</summary>
	public static bool IsSatisfiedBy(string? password) =>
		password is not null
		&& password.Length >= MinimumLength
		&& password.Any(char.IsLetter)
		&& password.Any(char.IsDigit);
}
