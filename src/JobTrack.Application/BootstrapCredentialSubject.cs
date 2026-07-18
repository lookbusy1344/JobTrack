namespace JobTrack.Application;

/// <summary>
///     Satisfies <c>IPasswordHasher&lt;TUser&gt;</c>'s generic parameter for the bootstrap command
///     (ADR 0005). No user entity exists yet at bootstrap time — this command is what creates the
///     first one — so there is no real <c>TUser</c> to pass. The default hasher never inspects its
///     user argument, so a stateless marker satisfies the contract without inventing a placeholder
///     user record. Consumers register <c>IPasswordHasher&lt;BootstrapCredentialSubject&gt;</c> (e.g.
///     <c>new PasswordHasher&lt;BootstrapCredentialSubject&gt;()</c>) at composition; only
///     <see cref="InstallationCommands" /> constructs an instance to pass to it.
/// </summary>
public sealed class BootstrapCredentialSubject
{
	internal BootstrapCredentialSubject()
	{
	}
}
