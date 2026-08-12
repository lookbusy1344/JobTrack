namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Hierarchy;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="ICostQueryPort" /> for application-slice tests. Materializes
///     whatever nodes/workers are seeded, unconditionally across the whole seeded database — mirroring
///     ADR 0017's internal elevated read scope, so tests can assert the exposure boundary is enforced
///     even though this fake performs no authorization-scoped filtering of its own.
/// </summary>
internal sealed class FakeCostQueryPort : ICostQueryPort
{
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _bulkSnapshotRoles = [];
	private readonly Dictionary<JobNodeId, HierarchyNode> _nodesById = [];
	private readonly Dictionary<JobNodeId, AppUserId?> _ownerByNodeId = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private readonly List<WorkerCostInputs> _workers = [];

	public int GetCostAccessInputsCallCount { get; private set; }

	public int GetCostInputsCallCount { get; private set; }

	public int GetBulkCostInputsCallCount { get; private set; }

	public Action? AfterGetBulkCostInputs { get; init; }

	public Action? AfterGetCostInputs { get; set; }

	public Task<CostAccessInputs> GetCostAccessInputsAsync(
		AppUserId actorId, JobNodeId nodeId, CancellationToken cancellationToken = default)
	{
		++GetCostAccessInputsCallCount;
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		var ownerIds = new List<AppUserId>();
		var current = (JobNodeId?)nodeId;
		while (current is JobNodeId id) {
			if (_ownerByNodeId.TryGetValue(id, out var owner) && owner is AppUserId ownerId) {
				ownerIds.Add(ownerId);
			}

			current = _nodesById.TryGetValue(id, out var node) ? node.ParentId : null;
		}

		return Task.FromResult(new CostAccessInputs {
			ActorRoles = actorRoles,
			AncestorOwnerIds = [.. ownerIds],
		});
	}

	public Task<CostQueryResult> GetCostInputsAsync(
		JobNodeId nodeId, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default)
	{
		++GetCostInputsCallCount;

		var result = new CostQueryResult {
			NodesById = EquatableDictionaryFactory.CopyOf(_nodesById),
			Bounds = new(Instant.MinValue, asOf),
			Workers = EquatableArray.CopyOf(_workers),
		};
		AfterGetCostInputs?.Invoke();

		return Task.FromResult(result);
	}

	public Task<BulkCostQueryResult> GetBulkCostInputsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> nodeIds, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default)
	{
		++GetBulkCostInputsCallCount;
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];
		if (_bulkSnapshotRoles.TryGetValue(actorId, out var snapshotRoles)) {
			roles = snapshotRoles;
		}

		var result = new BulkCostQueryResult {
			ActorRoles = roles,
			NodesById = EquatableDictionaryFactory.CopyOf(_nodesById),
			OwnerUserIdsById = EquatableDictionaryFactory.CopyOf(_ownerByNodeId),
			Bounds = new(Instant.MinValue, asOf),
			Workers = EquatableArray.CopyOf(_workers),
		};
		AfterGetBulkCostInputs?.Invoke();

		return Task.FromResult(result);
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedBulkSnapshotRoles(AppUserId actorId, params EmployeeRole[] roles) => _bulkSnapshotRoles[actorId] = [.. roles];

	public void SeedNode(HierarchyNode node) => _nodesById[node.Id] = node;

	/// <summary>Seeds <paramref name="nodeId" />'s owner for <see cref="GetCostAccessInputsAsync" /> (ADR 0040). Unseeded nodes have no owner.</summary>
	public void SeedOwner(JobNodeId nodeId, AppUserId ownerUserId) => _ownerByNodeId[nodeId] = ownerUserId;

	public void SeedWorker(WorkerCostInputs worker) => _workers.Add(worker);
}
