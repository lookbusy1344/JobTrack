namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Authorization;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IEmployeeCommandPort" /> for application-slice tests (plan §8.3).
///     Simulates the authorization guard a real persistence implementation must enforce inside its own
///     transaction, and the idempotent-no-op behaviour of assigning an already-held role, revoking an
///     unheld one, or setting an account's enabled state to its current value.
/// </summary>
internal sealed class FakeEmployeeCommandPort : IEmployeeCommandPort
{
	private readonly Dictionary<AppUserId, HourlyRate> _defaultHourlyRates = [];
	private readonly Dictionary<AppUserId, bool> _enabled = [];
	private readonly HashSet<JobNodeId> _existingNodes = [];
	private readonly Dictionary<AppUserId, JobNodeId?> _homeNode = [];
	private readonly HashSet<JobNodeId> _leafNodes = [];
	private readonly Dictionary<AppUserId, string> _passwordHash = [];
	private readonly Dictionary<AppUserId, bool> _requiresPasswordChange = [];
	private readonly Dictionary<AppUserId, List<EmployeeRole>> _roles = [];
	private readonly Dictionary<AppUserId, bool> _twoFactorEnabled = [];
	private readonly Dictionary<AppUserId, string> _userNames = [];
	private long _nextUserId = 1000;

	public CreateEmployeePersistenceRequest? LastCreateRequest { get; private set; }

	public Task<AccountStateResult> CreateEmployeeAsync(
		CreateEmployeePersistenceRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeAccountsOrThrow(request.Context.Actor);
		LastCreateRequest = request;

		if (_userNames.Values.Any(existing => string.Equals(existing, request.UserName, StringComparison.OrdinalIgnoreCase))) {
			throw new InvariantViolationException(
				"employee-username-already-taken", $"Username '{request.UserName}' is already taken.");
		}

		var userId = new AppUserId(_nextUserId++);
		_roles[userId] = [request.Role];
		_enabled[userId] = true;
		_requiresPasswordChange[userId] = true;
		_passwordHash[userId] = request.PasswordHash;
		_userNames[userId] = request.UserName;
		_defaultHourlyRates[userId] = request.DefaultHourlyRate ?? new HourlyRate(20m);

		return Task.FromResult(BuildAccountStateResult(userId, _roles[userId]));
	}

	public Task<EmployeeRolesResult> AssignRoleAsync(
		AssignEmployeeRoleRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeRolesOrThrow(request.Context.Actor);
		var roles = GetRolesOrThrow(request.TargetUserId);

		if (!roles.Contains(request.Role)) {
			roles.Add(request.Role);
		}

		return Task.FromResult(new EmployeeRolesResult { UserId = request.TargetUserId, Roles = [.. roles] });
	}

	public Task<EmployeeRolesResult> RevokeRoleAsync(
		RevokeEmployeeRoleRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeRolesOrThrow(request.Context.Actor);
		var roles = GetRolesOrThrow(request.TargetUserId);

		_ = roles.Remove(request.Role);

		return Task.FromResult(new EmployeeRolesResult { UserId = request.TargetUserId, Roles = [.. roles] });
	}

	public Task<AccountStateResult> SetEnabledAsync(
		SetEmployeeEnabledRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeAccountsOrThrow(request.Context.Actor);
		var roles = GetRolesOrThrow(request.TargetUserId);

		_enabled[request.TargetUserId] = request.Enabled;

		return Task.FromResult(BuildAccountStateResult(request.TargetUserId, roles));
	}

	public Task<EmployeeProfileResult> SetDefaultHourlyRateAsync(
		SetEmployeeDefaultHourlyRateRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeAccountsOrThrow(request.Context.Actor);
		_ = GetRolesOrThrow(request.TargetUserId);

		_defaultHourlyRates[request.TargetUserId] = request.DefaultHourlyRate;

		return Task.FromResult(new EmployeeProfileResult {
			Id = request.TargetUserId,
			DisplayName = _userNames[request.TargetUserId],
			IanaTimeZone = "Europe/London",
			DefaultHourlyRate = request.DefaultHourlyRate,
			HomeNodeId = _homeNode[request.TargetUserId],
			Version = 1,
		});
	}

	public Task<AccountStateResult> ResetPasswordAsync(
		ResetEmployeePasswordPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeAccountsOrThrow(request.Context.Actor);
		var roles = GetRolesOrThrow(request.TargetUserId);

		_passwordHash[request.TargetUserId] = request.PasswordHash;
		_requiresPasswordChange[request.TargetUserId] = true;

		return Task.FromResult(BuildAccountStateResult(request.TargetUserId, roles));
	}

	public Task<AccountStateResult> ResetTwoFactorAsync(
		ResetEmployeeTwoFactorRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeAccountsOrThrow(request.Context.Actor);
		var roles = GetRolesOrThrow(request.TargetUserId);

		_twoFactorEnabled[request.TargetUserId] = false;

		return Task.FromResult(BuildAccountStateResult(request.TargetUserId, roles));
	}

	public Task<EmployeeProfileResult> SetHomeNodeAsync(
		SetHomeNodeRequest request, CancellationToken cancellationToken = default)
	{
		_ = GetRolesOrThrow(request.Context.Actor);

		if (request.NodeId is JobNodeId nodeId) {
			if (!_existingNodes.Contains(nodeId)) {
				throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
			}

			if (_leafNodes.Contains(nodeId)) {
				throw new InvariantViolationException(
					"home-node-must-not-be-leaf", $"Job node {nodeId} is a leaf and cannot be set as a home node.");
			}
		}

		_homeNode[request.Context.Actor] = request.NodeId;

		return Task.FromResult(new EmployeeProfileResult {
			Id = request.Context.Actor,
			DisplayName = _userNames[request.Context.Actor],
			IanaTimeZone = "Europe/London",
			DefaultHourlyRate = _defaultHourlyRates[request.Context.Actor],
			HomeNodeId = request.NodeId,
			Version = 1,
		});
	}

	public void SeedRoles(AppUserId userId, params EmployeeRole[] roles)
	{
		_roles[userId] = [.. roles];
		_enabled.TryAdd(userId, true);
		_requiresPasswordChange.TryAdd(userId, false);
		_passwordHash.TryAdd(userId, "seed-hash");
		_userNames.TryAdd(userId, $"user-{userId.Value}");
		_defaultHourlyRates.TryAdd(userId, new(20m));
		_twoFactorEnabled.TryAdd(userId, false);
		_homeNode.TryAdd(userId, null);
	}

	/// <summary>
	///     Registers a job node the fake knows about for <see cref="SetHomeNodeAsync" />'s
	///     leaf-rejection check.
	/// </summary>
	public void SeedNode(JobNodeId nodeId, bool isLeaf)
	{
		_ = _existingNodes.Add(nodeId);
		if (isLeaf) {
			_ = _leafNodes.Add(nodeId);
		}
	}

	public void SeedTwoFactorEnabled(AppUserId userId) => _twoFactorEnabled[userId] = true;

	public bool IsTwoFactorEnabled(AppUserId userId) => _twoFactorEnabled[userId];

	private AccountStateResult BuildAccountStateResult(AppUserId userId, List<EmployeeRole> roles) =>
		new() {
			Id = userId,
			UserName = _userNames[userId],
			IsEnabled = _enabled[userId],
			RequiresPasswordChange = _requiresPasswordChange[userId],
			LockoutEnd = null,
			Roles = [.. roles],
		};

	private List<EmployeeRole> GetRolesOrThrow(AppUserId userId) =>
		_roles.TryGetValue(userId, out var roles) ? roles : throw new EntityNotFoundException($"Employee {userId} does not exist.");

	private void AuthorizeRolesOrThrow(AppUserId actorId)
	{
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];

		if (!EmployeeAccessPolicy.CanManageRoles(roles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage role assignments.");
		}
	}

	private void AuthorizeAccountsOrThrow(AppUserId actorId)
	{
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];

		if (!EmployeeAccessPolicy.CanManageAccounts(roles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage employee accounts.");
		}
	}
}
