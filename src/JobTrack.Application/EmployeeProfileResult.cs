namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IJobQueries.GetEmployeeProfileAsync" />.</summary>
public sealed record EmployeeProfileResult
{
	/// <summary>The employee's <c>app_user</c> identifier.</summary>
	public required AppUserId Id { get; init; }

	/// <summary>The employee's display name.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The employee's IANA time zone.</summary>
	public required string IanaTimeZone { get; init; }

	/// <summary>The employee's default hourly rate, if one applies before any override.</summary>
	public HourlyRate? DefaultHourlyRate { get; init; }

	/// <summary>
	///     The node this employee lands on after login instead of the tree root, or
	///     <see langword="null" /> for the default (root).
	/// </summary>
	public JobNodeId? HomeNodeId { get; init; }

	/// <summary>The employee's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
