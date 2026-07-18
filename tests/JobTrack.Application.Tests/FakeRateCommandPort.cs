namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Authorization;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IRateCommandPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases"). Simulates the authorization guard and overlap checks a real persistence
///     implementation must enforce inside its own transaction.
/// </summary>
internal sealed class FakeRateCommandPort : IRateCommandPort
{
	private readonly List<NodeRateOverrideResult> _nodeOverrides = [];
	private readonly HashSet<JobNodeId> _nodes = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private readonly List<UserCostRateResult> _userCostRates = [];
	private readonly HashSet<AppUserId> _users = [];
	private long _nextNodeOverrideId = 1;
	private long _nextUserCostRateId = 1;

	public Instant NowToReturn { get; set; } = Instant.FromUtc(2026, 1, 1, 0, 0);

	public Task<UserCostRateResult> AddUserCostRateAsync(
		AddUserCostRateRequest request, CancellationToken cancellationToken = default)
	{
		if (!_users.Contains(request.UserId)) {
			throw new EntityNotFoundException($"Employee {request.UserId} does not exist.");
		}

		AuthorizeOrThrow(request.Context.Actor);

		var overlaps = _userCostRates.Any(existing =>
			existing.UserId == request.UserId
			&& RangesOverlap(
				request.Rate.EffectiveStart, request.Rate.EffectiveEnd,
				existing.Rate.EffectiveStart, existing.Rate.EffectiveEnd));
		if (overlaps) {
			throw new InvariantViolationException(
				"user-cost-rate-overlap", "This cost rate's effective range overlaps another for this employee.");
		}

		var result = new UserCostRateResult {
			Id = new(_nextUserCostRateId++),
			UserId = request.UserId,
			Rate = request.Rate,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_userCostRates.Add(result);

		return Task.FromResult(result);
	}

	public Task<NodeRateOverrideResult> AddNodeRateOverrideAsync(
		AddNodeRateOverrideRequest request, CancellationToken cancellationToken = default)
	{
		if (!_users.Contains(request.UserId)) {
			throw new EntityNotFoundException($"Employee {request.UserId} does not exist.");
		}

		if (!_nodes.Contains(request.Override.NodeId)) {
			throw new EntityNotFoundException($"Job node {request.Override.NodeId} does not exist.");
		}

		AuthorizeOrThrow(request.Context.Actor);

		var overlaps = _nodeOverrides.Any(existing =>
			existing.UserId == request.UserId
			&& existing.Override.NodeId == request.Override.NodeId
			&& RangesOverlap(
				request.Override.EffectiveStart, request.Override.EffectiveEnd,
				existing.Override.EffectiveStart, existing.Override.EffectiveEnd));
		if (overlaps) {
			throw new InvariantViolationException(
				"node-rate-override-overlap", "This override's effective range overlaps another for this node and employee.");
		}

		var result = new NodeRateOverrideResult {
			Id = new(_nextNodeOverrideId++),
			UserId = request.UserId,
			Override = request.Override,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_nodeOverrides.Add(result);

		return Task.FromResult(result);
	}

	public Task<UserCostRateResult> CorrectUserCostRateAsync(
		CorrectUserCostRateRequest request, CancellationToken cancellationToken = default)
	{
		var index = _userCostRates.FindIndex(r => r.Id == request.RateId);
		if (index < 0) {
			throw new EntityNotFoundException($"User cost rate {request.RateId} does not exist.");
		}

		var existing = _userCostRates[index];
		if (request.UserId is { } expectedUserId && existing.UserId != expectedUserId) {
			throw new EntityNotFoundException($"Rate row {request.RateId} does not belong to employee {expectedUserId}.");
		}

		AuthorizeOrThrow(request.Context.Actor);
		if (existing.Version != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} but the current version is {existing.Version}.");
		}

		var overlaps = _userCostRates.Any(other =>
			other.Id != request.RateId
			&& other.UserId == existing.UserId
			&& RangesOverlap(
				request.Rate.EffectiveStart, request.Rate.EffectiveEnd,
				other.Rate.EffectiveStart, other.Rate.EffectiveEnd));
		if (overlaps) {
			throw new InvariantViolationException(
				"user-cost-rate-overlap", "This cost rate's effective range overlaps another for this employee.");
		}

		var result = existing with { Rate = request.Rate, ChangedAt = NowToReturn, Version = existing.Version + 1 };
		_userCostRates[index] = result;

		return Task.FromResult(result);
	}

	public Task<NodeRateOverrideResult> CorrectNodeRateOverrideAsync(
		CorrectNodeRateOverrideRequest request, CancellationToken cancellationToken = default)
	{
		var index = _nodeOverrides.FindIndex(o => o.Id == request.OverrideId);
		if (index < 0) {
			throw new EntityNotFoundException($"Node rate override {request.OverrideId} does not exist.");
		}

		var existing = _nodeOverrides[index];
		if (request.UserId is { } expectedUserId && existing.UserId != expectedUserId) {
			throw new EntityNotFoundException($"Rate row {request.OverrideId} does not belong to employee {expectedUserId}.");
		}

		if (!_nodes.Contains(request.Override.NodeId)) {
			throw new EntityNotFoundException($"Job node {request.Override.NodeId} does not exist.");
		}

		AuthorizeOrThrow(request.Context.Actor);
		if (existing.Version != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} but the current version is {existing.Version}.");
		}

		var overlaps = _nodeOverrides.Any(other =>
			other.Id != request.OverrideId
			&& other.UserId == existing.UserId
			&& other.Override.NodeId == request.Override.NodeId
			&& RangesOverlap(
				request.Override.EffectiveStart, request.Override.EffectiveEnd,
				other.Override.EffectiveStart, other.Override.EffectiveEnd));
		if (overlaps) {
			throw new InvariantViolationException(
				"node-rate-override-overlap", "This override's effective range overlaps another for this node and employee.");
		}

		var result = existing with { Override = request.Override, ChangedAt = NowToReturn, Version = existing.Version + 1 };
		_nodeOverrides[index] = result;

		return Task.FromResult(result);
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles)
	{
		_roles[actorId] = [.. roles];
		_users.Add(actorId);
	}

	public void SeedUser(AppUserId userId) => _users.Add(userId);

	public void SeedNode(JobNodeId nodeId) => _nodes.Add(nodeId);

	private static bool RangesOverlap(Instant aStart, Instant? aEnd, Instant bStart, Instant? bEnd)
	{
		var aEndValue = aEnd ?? Instant.MaxValue;
		var bEndValue = bEnd ?? Instant.MaxValue;

		return aStart < bEndValue && bStart < aEndValue;
	}

	private void AuthorizeOrThrow(AppUserId actorId)
	{
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];

		if (!RateAccessPolicy.CanManage(roles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage rate data.");
		}
	}
}
