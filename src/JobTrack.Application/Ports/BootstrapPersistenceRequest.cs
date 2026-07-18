namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Input to <see cref="IInstallationBootstrapPort.BootstrapAsync" />. Carries the credential already
///     hashed by <see cref="InstallationCommands" /> via the injected <c>IPasswordHasher&lt;T&gt;</c>
///     (ADR 0005) — the persistence implementation never sees plaintext.
/// </summary>
public sealed record BootstrapPersistenceRequest
{
	/// <summary>The new administrator's employee-profile display name (<c>app_user.display_name</c>).</summary>
	public required string DisplayName { get; init; }

	/// <summary>The new administrator's IANA time zone (<c>app_user.iana_time_zone</c>).</summary>
	public required string IanaTimeZone { get; init; }

	/// <summary>The new administrator's default hourly rate, if one applies before any override.</summary>
	public HourlyRate? DefaultHourlyRate { get; init; }

	/// <summary>The sign-in username (<c>identity_user.user_name</c>).</summary>
	public required string UserName { get; init; }

	/// <summary>The already-hashed credential (<c>identity_user.password_hash</c>).</summary>
	public required string PasswordHash { get; init; }

	/// <summary>The freshly generated security stamp (<c>identity_user.security_stamp</c>).</summary>
	public required string SecurityStamp { get; init; }
}
