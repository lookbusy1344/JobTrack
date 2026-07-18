namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommandPort.CreateEmployeeAsync" /> — carries an already-hashed credential.</summary>
public sealed record CreateEmployeePersistenceRequest
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

	/// <summary>The new account's already-hashed credential.</summary>
	public required string PasswordHash { get; init; }

	/// <summary>The new employee's initial role (ADR 0023).</summary>
	public required EmployeeRole Role { get; init; }
}
