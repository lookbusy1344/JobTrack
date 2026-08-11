namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Result of <see cref="IJobQueries.GetAccountStateAsync" />. Carries account state and role
///     assignments only — never the password hash, security stamp, or other authentication secrets
///     (spec §16), which stay confined to the persistence-owned credential port.
/// </summary>
public sealed record AccountStateResult
{
	/// <summary>The employee's <c>app_user</c> identifier.</summary>
	public required AppUserId Id { get; init; }

	/// <summary>The sign-in username.</summary>
	public required string UserName { get; init; }

	/// <summary>Whether the account can currently sign in.</summary>
	public required bool IsEnabled { get; init; }

	/// <summary>Whether the account must change its credential at next sign-in.</summary>
	public required bool RequiresPasswordChange { get; init; }

	/// <summary>The instant until which the account is locked out, if currently locked out.</summary>
	public Instant? LockoutEnd { get; init; }

	/// <summary>The employee's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> Roles { get; init; }
}
