namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Shared enforcement of <see cref="PasswordPolicy" />/<see cref="PasswordBlocklist" /> for every
///     command that sets a new credential (remediation plan §2.1, ADR 0056): self-service password
///     change, employee creation, employee password reset, and administrator bootstrap. Centralized
///     so every credential-setting route rejects the same passwords for the same reason, rather than
///     each command restating (or drifting from) the check.
/// </summary>
internal static class PasswordPolicyGuard
{
	/// <summary>The shared <see cref="InvariantViolationException.ConstraintId" /> for every rejection this guard raises.</summary>
	public const string ConstraintId = "account-new-password-policy";

	/// <summary>
	///     Throws <see cref="InvariantViolationException" /> (<see cref="ConstraintId" />) unless
	///     <paramref name="password" /> satisfies <see cref="PasswordPolicy" /> and is absent from
	///     <see cref="PasswordBlocklist" /> against <paramref name="username" />.
	/// </summary>
	public static void EnsureAcceptable(string? password, string? username)
	{
		if (!PasswordPolicy.IsSatisfiedBy(password)) {
			throw new InvariantViolationException(
				ConstraintId,
				$"The password must be between {PasswordPolicy.MinimumLength} and {PasswordPolicy.MaximumLength} characters.");
		}

		if (PasswordBlocklist.Contains(password, username)) {
			throw new InvariantViolationException(
				ConstraintId,
				"That password is too common or too easily guessed; choose a different one.");
		}
	}
}
