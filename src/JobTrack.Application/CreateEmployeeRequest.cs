namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IEmployeeCommands.CreateEmployeeAsync" /> (ADR 0023). The new account starts
///     enabled with a forced password change at next sign-in, holding exactly the one
///     <see
///         cref="Role" />
///     supplied here; further roles are granted or revoked afterward through
///     <see
///         cref="IEmployeeCommands.AssignRoleAsync" />
///     /<see cref="IEmployeeCommands.RevokeRoleAsync" />.
/// </summary>
public sealed record CreateEmployeeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The new employee's profile display name (<c>app_user.display_name</c>).</summary>
	public required string DisplayName { get; init; }

	/// <summary>The new employee's IANA time zone (<c>app_user.iana_time_zone</c>).</summary>
	public required string IanaTimeZone { get; init; }

	/// <summary>The new employee's default hourly rate, if one applies before any override.</summary>
	public HourlyRate? DefaultHourlyRate { get; init; }

	/// <summary>The sign-in username (<c>identity_user.user_name</c>); must be unique (ADR 0023).</summary>
	public required string UserName { get; init; }

	/// <summary>
	///     The plaintext initial credential, hashed by the command via the injected
	///     <c>IPasswordHasher&lt;T&gt;</c> — never persisted or logged as plaintext.
	/// </summary>
	public required string Password { get; init; }

	/// <summary>The new employee's initial role (ADR 0023).</summary>
	public required EmployeeRole Role { get; init; }
}
