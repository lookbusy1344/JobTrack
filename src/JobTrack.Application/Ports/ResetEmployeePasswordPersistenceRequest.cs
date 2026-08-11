namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Input to <see cref="IEmployeeCommandPort.ResetPasswordAsync" />. Carries the credential already
///     hashed by <see cref="EmployeeCommands" /> via the injected
///     <c>IPasswordHasher&lt;EmployeeCredentialSubject&gt;</c> — the persistence implementation never
///     sees plaintext (mirrors <see cref="BootstrapPersistenceRequest" />'s exact shape).
/// </summary>
internal sealed record ResetEmployeePasswordPersistenceRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose credential is being reset.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>The already-hashed credential (<c>identity_user.password_hash</c>).</summary>
	public required string PasswordHash { get; init; }
}
