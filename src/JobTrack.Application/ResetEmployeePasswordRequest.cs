namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IEmployeeCommands.ResetPasswordAsync" />. Reuses the same shape as
///     <see cref="BootstrapAdministratorRequest.Password" />: the plaintext credential, hashed by the
///     command via the injected <c>IPasswordHasher&lt;EmployeeCredentialSubject&gt;</c> — never
///     persisted or logged as plaintext.
/// </summary>
public sealed record ResetEmployeePasswordRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose credential is being reset.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>The new plaintext credential, hashed before it reaches persistence.</summary>
	public required string NewPassword { get; init; }
}
