namespace JobTrack.Application;

/// <summary>
///     Input to <see cref="ITokenCommands.TryAuthenticateAsync" />. Carries no <see cref="CommandContext" />
///     — by definition, the actor is not yet known before this call resolves (mirrors
///     <see cref="IInstallationCommands.BootstrapAdministratorAsync" />).
/// </summary>
public sealed record TryAuthenticatePersonalAccessTokenRequest
{
	/// <summary>The plaintext bearer credential presented by the caller.</summary>
	public required string Token { get; init; }
}
