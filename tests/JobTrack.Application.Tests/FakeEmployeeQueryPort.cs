namespace JobTrack.Application.Tests;

using Abstractions;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IEmployeeQueryPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases").
/// </summary>
internal sealed class FakeEmployeeQueryPort : IEmployeeQueryPort
{
	private readonly Dictionary<AppUserId, AccountStateResult> _accountStates = [];
	private readonly Dictionary<AppUserId, EmployeeProfileResult> _profiles = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private EquatableArray<EmployeeDirectoryEntry> _allEmployees = [];
	private EquatableArray<EmployeeDirectoryEntry> _directory = [];

	public int GetActorRolesCallCount { get; private set; }

	public int GetEmployeeProfileCallCount { get; private set; }

	public int GetAccountStateCallCount { get; private set; }

	public int GetEmployeeDirectoryCallCount { get; private set; }

	public int GetAllEmployeesCallCount { get; private set; }

	public Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default)
	{
		GetActorRolesCallCount++;
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		return Task.FromResult(actorRoles);
	}

	public Task<EmployeeProfileQueryResult> GetEmployeeProfileAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default)
	{
		GetEmployeeProfileCallCount++;
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		if (!_profiles.TryGetValue(targetUserId, out var profile)) {
			throw new EntityNotFoundException($"Employee {targetUserId} does not exist.");
		}

		return Task.FromResult(new EmployeeProfileQueryResult { ActorRoles = actorRoles, Profile = profile });
	}

	public Task<AccountStateQueryResult> GetAccountStateAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default)
	{
		GetAccountStateCallCount++;
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		if (!_accountStates.TryGetValue(targetUserId, out var accountState)) {
			throw new EntityNotFoundException($"Employee {targetUserId} does not exist.");
		}

		return Task.FromResult(new AccountStateQueryResult { ActorRoles = actorRoles, AccountState = accountState });
	}

	public Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryAsync(CancellationToken cancellationToken = default)
	{
		GetEmployeeDirectoryCallCount++;
		return Task.FromResult(_directory);
	}

	public Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesAsync(CancellationToken cancellationToken = default)
	{
		GetAllEmployeesCallCount++;
		return Task.FromResult(_allEmployees);
	}

	/// <summary>
	///     Seeds only the actor's roles, for tests that need role resolution without a full
	///     profile/account-state fixture (e.g. the per-node cost filter, ADR 0042).
	/// </summary>
	public void SeedRoles(AppUserId id, EquatableArray<EmployeeRole> roles) => _roles[id] = roles;

	public void Seed(
		AppUserId id, EmployeeProfileResult profile, AccountStateResult accountState, EquatableArray<EmployeeRole> roles)
	{
		_profiles[id] = profile;
		_accountStates[id] = accountState;
		_roles[id] = roles;
	}

	public void SeedDirectory(EquatableArray<EmployeeDirectoryEntry> directory) => _directory = directory;

	public void SeedAllEmployees(EquatableArray<EmployeeDirectoryEntry> allEmployees) => _allEmployees = allEmployees;
}
