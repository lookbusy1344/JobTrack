namespace JobTrack.Application;

/// <summary>
///     Input to <see cref="IJobQueries.GetAllEmployeesAsync" />. Carries no authorization gate of its
///     own — see <see cref="EmployeeDirectoryEntry" />.
/// </summary>
public sealed record GetAllEmployeesRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }
}
