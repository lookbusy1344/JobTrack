namespace JobTrack.Application;

/// <summary>
///     Satisfies <c>IPasswordHasher&lt;TUser&gt;</c>'s generic parameter for administrator-driven
///     employee password resets, mirroring <see cref="BootstrapCredentialSubject" />'s exact shape and
///     justification: the default hasher never inspects its user argument, so a stateless marker
///     satisfies the contract without inventing a placeholder user record. Consumers register
///     <c>IPasswordHasher&lt;EmployeeCredentialSubject&gt;</c> at composition; only
///     <see cref="EmployeeCommands" /> constructs an instance to pass to it.
/// </summary>
public sealed class EmployeeCredentialSubject
{
	internal EmployeeCredentialSubject()
	{
	}
}
