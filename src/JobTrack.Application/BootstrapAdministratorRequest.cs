namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IInstallationCommands.BootstrapAdministratorAsync" />. Carries no actor
///     (ADR 0005: by definition no administrator yet exists) or <c>version</c> (there is nothing yet to
///     compare-and-swap against); every other command in the facade carries both.
/// </summary>
public sealed record BootstrapAdministratorRequest
{
	/// <summary>The new administrator's employee-profile display name (<c>app_user.display_name</c>).</summary>
	public required string DisplayName { get; init; }

	/// <summary>The new administrator's IANA time zone (<c>app_user.iana_time_zone</c>).</summary>
	public required string IanaTimeZone { get; init; }

	/// <summary>The new administrator's default hourly rate, if one applies before any override.</summary>
	public HourlyRate? DefaultHourlyRate { get; init; }

	/// <summary>The sign-in username (<c>identity_user.user_name</c>).</summary>
	public required string UserName { get; init; }

	/// <summary>
	///     The plaintext initial credential, hashed by the command via the injected
	///     <c>IPasswordHasher&lt;T&gt;</c> (ADR 0005) — never persisted or logged as plaintext.
	/// </summary>
	public required string Password { get; init; }

	/// <summary>Correlates this request with the audit events it produces.</summary>
	public required Guid CorrelationId { get; init; }
}
