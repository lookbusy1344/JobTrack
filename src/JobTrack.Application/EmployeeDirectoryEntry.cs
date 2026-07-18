namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     One row of <see cref="IJobQueries.GetEmployeeDirectoryAsync" /> — the subset of an employee's
///     identity safe to show to any viewer alongside job ownership (spec §7.3: job data is an
///     unqualified baseline capability for every role). Deliberately narrower than
///     <see cref="EmployeeProfileResult" />, which also carries <see cref="EmployeeProfileResult.DefaultHourlyRate" />
///     and is gated to the employee themselves or an <see cref="EmployeeRole.Administrator" /> by
///     <see cref="Domain.Authorization.EmployeeAccessPolicy.CanViewEmployee" />.
/// </summary>
public sealed record EmployeeDirectoryEntry
{
	/// <summary>The employee's <c>app_user</c> identifier.</summary>
	public required AppUserId Id { get; init; }

	/// <summary>The employee's display name.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The employee's login username.</summary>
	public required string UserName { get; init; }
}
