namespace JobTrack.Application.Tests;

using Abstractions;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IRateQueryPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases").
/// </summary>
internal sealed class FakeRateQueryPort : IRateQueryPort
{
	private readonly Dictionary<AppUserId, List<NodeRateOverrideResult>> _nodeRateOverrides = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private readonly Dictionary<AppUserId, List<UserCostRateResult>> _userCostRates = [];

	public Task<RateQueryResult> GetRatesAsync(
		AppUserId actorId, AppUserId userId, CancellationToken cancellationToken = default)
	{
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		if (!_userCostRates.TryGetValue(userId, out var userCostRates)) {
			throw new EntityNotFoundException($"Employee {userId} does not exist.");
		}

		return Task.FromResult(new RateQueryResult {
			ActorRoles = actorRoles,
			UserCostRates = [.. userCostRates],
			NodeRateOverrides = [.. _nodeRateOverrides[userId]],
		});
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedEmployee(AppUserId userId)
	{
		_userCostRates.TryAdd(userId, []);
		_nodeRateOverrides.TryAdd(userId, []);
	}

	public void SeedUserCostRate(UserCostRateResult rate)
	{
		SeedEmployee(rate.UserId);
		_userCostRates[rate.UserId].Add(rate);
	}

	public void SeedNodeRateOverride(NodeRateOverrideResult overrideResult)
	{
		SeedEmployee(overrideResult.UserId);
		_nodeRateOverrides[overrideResult.UserId].Add(overrideResult);
	}
}
