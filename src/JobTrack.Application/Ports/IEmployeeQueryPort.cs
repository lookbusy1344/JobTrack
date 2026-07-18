namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries" />'s employee profile and
///     account-state queries (plan §7.3 step 2). Loads authoritative data for both the actor (roles
///     only) and the target (profile or account state) in one round-trip.
/// </summary>
public interface IEmployeeQueryPort
{
	/// <summary>Loads the actor's current roles for an authorization pre-check before target employee data.</summary>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default);

	/// <summary>Loads the target employee's profile and the actor's current roles.</summary>
	/// <exception cref="EntityNotFoundException">The actor or target employee does not exist.</exception>
	Task<EmployeeProfileQueryResult> GetEmployeeProfileAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default);

	/// <summary>Loads the target employee's account state and the actor's current roles.</summary>
	/// <exception cref="EntityNotFoundException">The actor or target employee does not exist.</exception>
	Task<AccountStateQueryResult> GetAccountStateAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads every enabled workflow employee's directory-visible identity — see
	///     <see cref="IJobQueries.GetEmployeeDirectoryAsync" />.
	/// </summary>
	Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryAsync(CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads every employee's directory-visible identity, any role, enabled or not — see
	///     <see cref="IJobQueries.GetAllEmployeesAsync" />.
	/// </summary>
	Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesAsync(CancellationToken cancellationToken = default);
}
