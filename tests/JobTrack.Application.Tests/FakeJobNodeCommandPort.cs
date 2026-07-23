namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Authorization;
using Domain.Hierarchy;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IJobNodeCommandPort" />, <see cref="IReadinessQueryPort" />,
///     <see cref="IJobBrowseQueryPort" />, and <see cref="IAwaitingProgressQueryPort" /> for
///     application-slice tests (plan §7.3: "write application tests with fake ports, then provider
///     conformance tests using real databases"). Simulates the authorization guard and hierarchy-cycle
///     check a real persistence implementation must enforce inside its own transaction. Implements all
///     four ports over one shared node graph so tests can seed data once and exercise commands and
///     queries against the same state.
/// </summary>
internal sealed class FakeJobNodeCommandPort : IJobNodeCommandPort, IReadinessQueryPort, IJobBrowseQueryPort, IAwaitingProgressQueryPort
{
	private readonly Dictionary<JobNodeId, LeafWorkResult> _leafWork = [];
	private readonly Dictionary<JobNodeId, JobNodeResult> _nodes = [];
	private readonly HashSet<(JobNodeId RequiredJobId, JobNodeId DependentJobId)> _prerequisites = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private readonly HashSet<JobNodeId> _undeletable = [];
	private readonly HashSet<JobNodeId> _workedLeafIds = [];
	private long _nextId = 2;

	public Instant NowToReturn { get; set; } = Instant.FromUtc(2026, 1, 1, 0, 0);

	/// <summary>
	///     The node specs the most recent <see cref="ImportSubtreeAsync" /> received, in the order
	///     <see cref="JobCommands.ImportSubtreeAsync" /> handed them over — lets a test assert what the
	///     application layer passed down, including each node's <see cref="ImportSubtreeNodeSpec.LeafWork" />.
	/// </summary>
	public EquatableArray<ImportSubtreeNodeSpec> LastImportedNodes { get; private set; }

	/// <summary>
	///     The roles seeded here, so a test SUT can seed the employee port with the same set —
	///     in production every port reads one database, so the fakes must not disagree about roles.
	/// </summary>
	public IReadOnlyDictionary<AppUserId, EquatableArray<EmployeeRole>> SeededRoles => _roles;

	public Task<AwaitingProgressQueryResult> GetAwaitingProgressInputsAsync(CancellationToken cancellationToken = default)
	{
		var nodesById = new Dictionary<JobNodeId, HierarchyNode>();
		var factsById = new Dictionary<JobNodeId, AwaitingProgressNodeFacts>();
		foreach (var node in _nodes.Values) {
			var childIds = _nodes.Values.Where(n => n.ParentId == node.Id).Select(n => n.Id).ToArray();
			var leafAchievement = node.Kind == NodeKind.Leaf && _leafWork.TryGetValue(node.Id, out var leafWork)
				? leafWork.Achievement
				: (Achievement?)null;
			nodesById[node.Id] = new(node.Id, node.ParentId, [.. childIds], leafAchievement);
			factsById[node.Id] = new(
				node.Id, node.Description, node.OwnerUserId, node.Priority, node.NeededStart, node.NeededFinish, node.ArchivedAt);
		}

		var prerequisites = _prerequisites.Select(edge => new PrerequisiteEdge(edge.RequiredJobId, edge.DependentJobId));

		return Task.FromResult(new AwaitingProgressQueryResult {
			NodesById = EquatableDictionaryFactory.CopyOf(nodesById),
			FactsById = EquatableDictionaryFactory.CopyOf(factsById),
			Prerequisites = [.. prerequisites],
		});
	}

	public Task<JobNodeDetailResult> GetNodeAsync(JobNodeId? nodeId, CancellationToken cancellationToken = default)
	{
		JobNodeResult node;
		if (nodeId is JobNodeId id) {
			node = GetExisting(id);
		} else {
			node = _nodes.Values.SingleOrDefault(n => n.ParentId is null)
				   ?? throw new EntityNotFoundException("No root job node exists.");
		}

		var ancestors = new List<JobNodeAncestorResult>();
		var current = node.ParentId;
		while (current is JobNodeId ancestorId) {
			var ancestor = GetExisting(ancestorId);
			var enrichedAncestor = WithStructuralFacts(ancestor);
			ancestors.Insert(0, new(enrichedAncestor.Id, enrichedAncestor.Description, enrichedAncestor.Kind));
			current = ancestor.ParentId;
		}

		return Task.FromResult(new JobNodeDetailResult { Node = WithStructuralFacts(node), Ancestors = [.. ancestors] });
	}

	public Task<EquatableArray<JobNodeSummaryResult>> GetChildrenAsync(
		JobNodeId parentId, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		_ = GetExisting(parentId);

		var children = _nodes.Values
			.Where(n => n.ParentId == parentId)
			.Where(n => ownership.Matches(n.OwnerUserId))
			.Where(n => MatchesArchiveFilter(n, archiveFilter))
			.OrderBy(n => n.Id.Value)
			.Select(ToSummary);

		return Task.FromResult<EquatableArray<JobNodeSummaryResult>>([.. ApplyPaging(children, offset, limit)]);
	}

	public Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesAsync(
		string searchText, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		var matches = _nodes.Values
			.Where(n => n.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase))
			.Where(n => ownership.Matches(n.OwnerUserId))
			.Where(n => MatchesArchiveFilter(n, archiveFilter))
			.OrderBy(n => n.Id.Value)
			.Select(ToSummary);

		return Task.FromResult<EquatableArray<JobNodeSummaryResult>>([.. ApplyPaging(matches, offset, limit)]);
	}

	public Task<EquatableArray<JobNodeSummaryResult>> GetSummariesByIdsAsync(
		EquatableArray<JobNodeId> ids, CancellationToken cancellationToken = default)
	{
		var idSet = ids.ToHashSet();
		var summaries = _nodes.Values.Where(n => idSet.Contains(n.Id)).Select(ToSummary);

		return Task.FromResult<EquatableArray<JobNodeSummaryResult>>([.. summaries]);
	}

	public Task<EquatableArray<JobNodeSubtreeRow>> GetSubtreeAsync(
		JobNodeId rootId, int maxDepth, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		CancellationToken cancellationToken = default)
	{
		if (maxDepth < 0 || maxDepth > JobSubtreeLimits.HardMaxDepth) {
			throw new ArgumentOutOfRangeException(
				nameof(maxDepth), maxDepth, $"Subtree depth must be between 0 and {JobSubtreeLimits.HardMaxDepth}.");
		}

		_ = GetExisting(rootId);

		var depthById = new Dictionary<JobNodeId, int> { [rootId] = 0 };
		var expandedById = new Dictionary<JobNodeId, bool>();
		var toExpand = new Queue<JobNodeId>();
		toExpand.Enqueue(rootId);

		while (toExpand.Count > 0) {
			var currentId = toExpand.Dequeue();
			var currentDepth = depthById[currentId];
			var isRoot = currentId == rootId;
			var canExpand = currentDepth < maxDepth;
			expandedById[currentId] = canExpand;

			if (!canExpand) {
				continue;
			}

			var children = _nodes.Values.Where(n => n.ParentId == currentId).OrderBy(n => n.Id.Value).ToList();
			for (var rank = 0; rank < children.Count; rank++) {
				var child = children[rank];
				depthById[child.Id] = currentDepth + 1;

				if (isRoot || rank < JobSubtreeLimits.BreadthCap) {
					toExpand.Enqueue(child.Id);
				} else {
					expandedById[child.Id] = false;
				}
			}
		}

		var rows = depthById.Keys.Select(GetExisting).Select(WithStructuralFacts).ToList();
		var matchesById = rows.ToDictionary(n => n.Id, n => ownership.Matches(n.OwnerUserId) && MatchesArchiveFilter(n, archiveFilter));

		var childrenByParent = rows
			.Where(n => n.ParentId is JobNodeId p && depthById.ContainsKey(p))
			.GroupBy(n => n.ParentId!.Value)
			.ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList());

		var keepById = new Dictionary<JobNodeId, bool>();
		foreach (var node in rows.OrderByDescending(n => depthById[n.Id])) {
			var descendantMatches = childrenByParent.TryGetValue(node.Id, out var childIds) && childIds.Any(c => keepById[c]);
			keepById[node.Id] = matchesById[node.Id] || descendantMatches;
		}

		var result = rows
			.Where(n => keepById[n.Id])
			.OrderBy(n => n.Id.Value)
			.Select(n => new JobNodeSubtreeRow {
				Id = n.Id,
				ParentId = n.ParentId,
				Kind = n.Kind,
				Depth = depthById[n.Id],
				Description = n.Description,
				OwnerUserId = n.OwnerUserId,
				Priority = n.Priority,
				ArchivedAt = n.ArchivedAt,
				HasChildren = n.HasChildren,
				HasLeafWork = n.HasLeafWork,
				HasUnexpandedChildren = n.HasChildren && !expandedById[n.Id],
				MatchesFilter = matchesById[n.Id],
			});

		return Task.FromResult<EquatableArray<JobNodeSubtreeRow>>([.. result]);
	}

	public Task<JobNodeResult> AddChildAsync(CreateJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(Create(request));
	}

	public Task<JobNodeResult> EditAsync(EditJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.NodeId);
		AuthorizeOrThrow(request.Context.Actor, request.NodeId);
		CheckVersionOrThrow(existing.Version, request.Version);

		if (existing.ParentId is null && request.OwnerUserId is null) {
			throw new InvariantViolationException(
				"job-node-root-owner-required", "The permanent root's owner cannot be null.");
		}

		var updated = existing with {
			Description = request.Description,
			WriteUp = request.WriteUp,
			OwnerUserId = request.OwnerUserId,
			ExpectedDurationHours = request.ExpectedDurationHours,
			ExpectedCost = request.ExpectedCost,
			NeededStart = request.NeededStart,
			NeededFinish = request.NeededFinish,
			Priority = request.Priority,
			Version = existing.Version + 1,
		};
		_nodes[updated.Id] = updated;

		return Task.FromResult(WithStructuralFacts(updated));
	}

	public Task<JobNodeResult> MoveAsync(MoveJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.NodeId);
		_ = GetExisting(request.NewParentId);
		AuthorizeOrThrow(request.Context.Actor, request.NodeId);
		AuthorizeOrThrow(request.Context.Actor, request.NewParentId);
		CheckVersionOrThrow(existing.Version, request.Version);

		if (request.NewParentId == request.NodeId || IsDescendantOf(request.NewParentId, request.NodeId)) {
			throw new InvariantViolationException(
				"job-node-move-would-cycle", "Moving this node under the requested parent would create a cycle.");
		}

		var updated = existing with { ParentId = request.NewParentId, Version = existing.Version + 1 };
		_nodes[updated.Id] = updated;

		return Task.FromResult(WithStructuralFacts(updated));
	}

	public Task<JobNodeResult> ArchiveAsync(ArchiveJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.NodeId);
		AuthorizeOrThrow(request.Context.Actor, request.NodeId);
		CheckVersionOrThrow(existing.Version, request.Version);

		var updated = existing with { ArchivedAt = NowToReturn, Version = existing.Version + 1 };
		_nodes[updated.Id] = updated;

		return Task.FromResult(WithStructuralFacts(updated));
	}

	public Task<JobNodeResult> PickUpAsync(PickUpJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.NodeId);

		if (!JobPickupPolicy.CanPickUp(RolesOf(request.Context.Actor), true)) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not pick up job node {request.NodeId}.");
		}

		if (existing.OwnerUserId is not null) {
			throw new InvariantViolationException(
				"job-node-already-claimed", $"Job node {request.NodeId} has already been claimed.");
		}

		var updated = existing with { OwnerUserId = request.Context.Actor, Version = existing.Version + 1 };
		_nodes[updated.Id] = updated;

		return Task.FromResult(WithStructuralFacts(updated));
	}

	public Task DeleteAsync(DeleteJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.NodeId);
		AuthorizeOrThrow(request.Context.Actor, request.NodeId);
		CheckVersionOrThrow(existing.Version, request.Version);

		if (_undeletable.Contains(request.NodeId)) {
			throw new InvariantViolationException(
				"job-node-not-deletable", "This job node cannot be deleted because it has dependent data.");
		}

		if (existing.ParentId is null) {
			throw new InvariantViolationException("job-node-is-root-cannot-delete", "The root job node cannot be deleted.");
		}

		if (_nodes.Values.Any(n => n.ParentId == request.NodeId)) {
			throw new InvariantViolationException(
				"job-node-has-children-cannot-delete",
				"A node with children cannot be deleted; delete or move its children first.");
		}

		if (_prerequisites.Any(edge => edge.RequiredJobId == request.NodeId || edge.DependentJobId == request.NodeId)) {
			throw new InvariantViolationException(
				"job-node-has-prerequisites-cannot-delete",
				"A node with a prerequisite edge cannot be deleted; remove the edge(s) first.");
		}

		if (_leafWork.ContainsKey(request.NodeId)) {
			if (_workedLeafIds.Contains(request.NodeId)) {
				if (!JobNodeDeletePolicy.CanForceDeleteWorkedLeaf(RolesOf(request.Context.Actor))) {
					throw new AuthorizationDeniedException(
						$"Actor {request.Context.Actor} may not delete job node {request.NodeId}: it has worked session " +
						"history and deletion requires the Administrator role (ADR 0036).");
				}

				if (string.IsNullOrWhiteSpace(request.Reason)) {
					throw new InvariantViolationException(
						"job-node-delete-worked-leaf-reason-required",
						"Deleting a leaf with worked session history requires a reason.");
				}
			}

			_leafWork.Remove(request.NodeId);
		}

		_nodes.Remove(request.NodeId);

		return Task.CompletedTask;
	}

	public Task<LeafWorkResult> AttachLeafWorkAsync(AttachLeafWorkRequest request, CancellationToken cancellationToken = default)
	{
		var node = GetExisting(request.JobNodeId);
		AuthorizeOrThrow(request.Context.Actor, request.JobNodeId);

		if (node.ParentId is null) {
			throw new InvariantViolationException(
				"job-node-is-root-cannot-attach-leaf-work", "The root job node cannot hold LeafWork.");
		}

		var enriched = WithStructuralFacts(node);
		if (enriched.HasChildren) {
			throw new InvariantViolationException(
				"job-node-has-children-cannot-attach-leaf-work", "A node with children cannot hold LeafWork.");
		}

		if (_leafWork.ContainsKey(request.JobNodeId)) {
			throw new InvariantViolationException("leaf-work-already-attached", "This node already has LeafWork attached.");
		}

		var leafWork = new LeafWorkResult {
			JobNodeId = request.JobNodeId,
			Achievement = Achievement.Waiting,
			PartialCriteria = request.PartialCriteria,
			FullCriteria = request.FullCriteria,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_leafWork[request.JobNodeId] = leafWork;

		return Task.FromResult(leafWork);
	}

	public Task<DecomposeWorkedLeafResult> DecomposeWorkedLeafAsync(
		DecomposeWorkedLeafRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.LeafNodeId);
		AuthorizeOrThrow(request.Context.Actor, request.LeafNodeId);
		CheckVersionOrThrow(existing.Version, request.Version);

		if (!_leafWork.TryGetValue(request.LeafNodeId, out var leafWork)) {
			throw new InvariantViolationException("leaf-work-not-attached", "This node has no LeafWork to decompose.");
		}

		var existingWorkChild = WithStructuralFacts(new() {
			Id = new(_nextId++),
			ParentId = existing.Id,
			Kind = NodeKind.Leaf,
			Description = request.ExistingWorkDescription,
			PostedByUserId = request.Context.Actor,
			OwnerUserId = existing.OwnerUserId,
			Priority = existing.Priority,
			PostedAt = NowToReturn,
			Version = 1,
			HasChildren = false,
			HasLeafWork = false,
		});
		_nodes[existingWorkChild.Id] = existingWorkChild;

		_leafWork.Remove(request.LeafNodeId);
		_leafWork[existingWorkChild.Id] = leafWork with { JobNodeId = existingWorkChild.Id, Version = leafWork.Version + 1 };

		var newChildIds = new List<JobNodeId>();
		foreach (var child in request.NewChildren) {
			var newChild = WithStructuralFacts(new() {
				Id = new(_nextId++),
				ParentId = existing.Id,
				Kind = NodeKind.Leaf,
				Description = child.Description,
				WriteUp = child.WriteUp,
				PostedByUserId = request.Context.Actor,
				OwnerUserId = child.OwnerUserId,
				ExpectedDurationHours = child.ExpectedDurationHours,
				ExpectedCost = child.ExpectedCost,
				NeededStart = child.NeededStart,
				NeededFinish = child.NeededFinish,
				Priority = child.Priority,
				PostedAt = NowToReturn,
				Version = 1,
				HasChildren = false,
				HasLeafWork = false,
			});
			_nodes[newChild.Id] = newChild;
			newChildIds.Add(newChild.Id);
		}

		var convertedBranch = WithStructuralFacts(existing with { Description = request.BranchDescription, Version = existing.Version + 1 });
		_nodes[convertedBranch.Id] = convertedBranch;

		return Task.FromResult(new DecomposeWorkedLeafResult {
			BranchId = convertedBranch.Id,
			BranchVersion = convertedBranch.Version,
			ExistingWorkChildId = existingWorkChild.Id,
			NewChildIds = [.. newChildIds],
		});
	}

	public Task AddPrerequisiteAsync(AddPrerequisiteRequest request, CancellationToken cancellationToken = default)
	{
		AddPrerequisiteCore(request.Context.Actor, request.RequiredJobId, request.DependentJobId);
		return Task.CompletedTask;
	}

	public Task<ImportSubtreeResult> ImportSubtreeAsync(ImportSubtreeRequest request, CancellationToken cancellationToken = default)
	{
		_ = GetExisting(request.ParentId);
		AuthorizeOrThrow(request.Context.Actor, request.ParentId);

		var createdByLocalId = new Dictionary<long, JobNodeId>(request.Nodes.Count);
		var created = new List<ImportedJobNode>(request.Nodes.Count);

		foreach (var spec in request.Nodes) {
			var parentId = spec.ParentLocalId.HasValue ? createdByLocalId[spec.ParentLocalId.Value] : request.ParentId;
			var node = Create(new() {
				Context = request.Context,
				ParentId = parentId,
				Description = spec.Description,
				WriteUp = spec.WriteUp,
				OwnerUserId = spec.OwnerUserId,
				ExpectedDurationHours = spec.ExpectedDurationHours,
				ExpectedCost = spec.ExpectedCost,
				NeededStart = spec.NeededStart,
				NeededFinish = spec.NeededFinish,
				Priority = spec.Priority,
			});
			createdByLocalId[spec.LocalId] = node.Id;
			created.Add(new() { LocalId = spec.LocalId, JobNodeId = node.Id });
		}

		foreach (var spec in request.Nodes) {
			var dependentId = createdByLocalId[spec.LocalId];
			foreach (var prerequisiteLocalId in spec.PrerequisiteLocalIds) {
				AddPrerequisiteCore(request.Context.Actor, createdByLocalId[prerequisiteLocalId], dependentId);
			}
		}

		// Replay the batch's recorded work chronologically, the same order the real ports use, so a
		// dependent leaf's gate is evaluated only after its prerequisites have already been closed.
		foreach (var spec in request.Nodes
					 .Where(n => n.LeafWork is not null)
					 .OrderBy(n => EarliestImportedSessionStart(n.LeafWork!))
					 .ThenBy(n => n.LocalId)) {
			var nodeId = createdByLocalId[spec.LocalId];
			var work = spec.LeafWork!;

			_ = EnsureLeafWorkAttached(nodeId, request.Context.Actor);
			_ = _workedLeafIds.Add(nodeId);
			_leafWork[nodeId] = _leafWork[nodeId] with { Achievement = work.Achievement, ChangedAt = NowToReturn };
		}

		LastImportedNodes = request.Nodes;
		return Task.FromResult(new ImportSubtreeResult { Nodes = [.. created] });
	}

	public Task RemovePrerequisiteAsync(RemovePrerequisiteRequest request, CancellationToken cancellationToken = default)
	{
		_ = GetExisting(request.RequiredJobId);
		_ = GetExisting(request.DependentJobId);
		AuthorizeOrThrow(request.Context.Actor, request.RequiredJobId);
		AuthorizeOrThrow(request.Context.Actor, request.DependentJobId);

		if (!_prerequisites.Remove((request.RequiredJobId, request.DependentJobId))) {
			throw new EntityNotFoundException(
				$"No prerequisite edge {request.RequiredJobId} -> {request.DependentJobId} exists.");
		}

		return Task.CompletedTask;
	}

	public Task<ReadinessQueryResult> GetReadinessInputsAsync(JobNodeId nodeId, CancellationToken cancellationToken = default)
	{
		_ = GetExisting(nodeId);

		var nodesById = new Dictionary<JobNodeId, HierarchyNode>();
		foreach (var node in _nodes.Values) {
			var childIds = _nodes.Values.Where(n => n.ParentId == node.Id).Select(n => n.Id).ToArray();
			var leafAchievement = node.Kind == NodeKind.Leaf && _leafWork.TryGetValue(node.Id, out var leafWork)
				? leafWork.Achievement
				: (Achievement?)null;
			nodesById[node.Id] = new(node.Id, node.ParentId, [.. childIds], leafAchievement);
		}

		var prerequisites = _prerequisites.Select(edge => new PrerequisiteEdge(edge.RequiredJobId, edge.DependentJobId));

		return Task.FromResult(new ReadinessQueryResult {
			NodesById = EquatableDictionaryFactory.CopyOf(nodesById),
			Prerequisites = [.. prerequisites],
		});
	}

	/// <summary>
	///     ADR 0048: mirrors <see cref="PickUpAsync" />'s claim, but for <paramref name="workedByUserId" />
	///     rather than the acting caller, and silently no-ops (rather than throwing) when the node is
	///     already owned or the actor isn't eligible to pick up -- <see cref="FakeWorkSessionCommandPort" />
	///     calls this before its own authorization check runs.
	/// </summary>
	internal void AutoClaimUnassignedNode(AppUserId actorId, JobNodeId nodeId, AppUserId workedByUserId)
	{
		var existing = GetExisting(nodeId);
		if (existing.OwnerUserId is not null || !JobPickupPolicy.CanPickUp(RolesOf(actorId), true)) {
			return;
		}

		_nodes[nodeId] = existing with { OwnerUserId = workedByUserId, Version = existing.Version + 1 };
	}

	private static Instant EarliestImportedSessionStart(ImportSubtreeLeafWorkSpec work) =>
		work.AdditionalSessions.Select(session => session.StartedAt).Prepend(work.StartedAt).Min();

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedNode(JobNodeResult node)
	{
		_nodes[node.Id] = node;
		RefreshStructuralFacts(node.Id);
		if (node.ParentId is JobNodeId parentId) {
			RefreshStructuralFacts(parentId);
		}
	}

	public void MarkUndeletable(JobNodeId id) => _undeletable.Add(id);

	/// <summary>
	///     Marks an already-attached <c>LeafWork</c> as having <c>WorkSession</c> history
	///     (ADR 0036), so <see cref="DeleteAsync" /> requires the Administrator role and a reason to
	///     delete it, mirroring the real ports without modeling actual session rows.
	/// </summary>
	public void MarkLeafWorked(JobNodeId id) => _workedLeafIds.Add(id);

	public LeafWorkResult? FindLeafWork(JobNodeId id) => _leafWork.TryGetValue(id, out var leafWork) ? leafWork : null;

	public JobNodeResult? FindNode(JobNodeId id) => _nodes.TryGetValue(id, out var node) ? node : null;

	public void SetLeafWork(LeafWorkResult leafWork) => _leafWork[leafWork.JobNodeId] = leafWork;

	/// <summary>
	///     Attaches <c>LeafWork</c> if <paramref name="jobNodeId" /> doesn't already have it,
	///     replicating <see cref="AttachLeafWorkAsync" />'s validation but returning the existing
	///     <c>LeafWork</c> idempotently instead of throwing "already attached" -- backs
	///     <see cref="FakeWorkSessionCommandPort.StartWorkAsync" />'s attach-if-missing step.
	/// </summary>
	public LeafWorkResult EnsureLeafWorkAttached(JobNodeId jobNodeId, AppUserId actorId)
	{
		var node = GetExisting(jobNodeId);
		AuthorizeOrThrow(actorId, jobNodeId);

		if (_leafWork.TryGetValue(jobNodeId, out var existing)) {
			return existing;
		}

		if (node.ParentId is null) {
			throw new InvariantViolationException(
				"job-node-is-root-cannot-attach-leaf-work", "The root job node cannot hold LeafWork.");
		}

		if (WithStructuralFacts(node).HasChildren) {
			throw new InvariantViolationException(
				"job-node-has-children-cannot-attach-leaf-work", "A node with children cannot hold LeafWork.");
		}

		var leafWork = new LeafWorkResult {
			JobNodeId = jobNodeId,
			Achievement = Achievement.Waiting,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_leafWork[jobNodeId] = leafWork;

		return leafWork;
	}

	public EquatableArray<EmployeeRole> RolesOf(AppUserId actorId) =>
		_roles.TryGetValue(actorId, out var roles) ? roles : [];

	private void AddPrerequisiteCore(AppUserId actorId, JobNodeId requiredJobId, JobNodeId dependentJobId)
	{
		_ = GetExisting(requiredJobId);
		_ = GetExisting(dependentJobId);
		AuthorizeOrThrow(actorId, requiredJobId);
		AuthorizeOrThrow(actorId, dependentJobId);

		if (requiredJobId == dependentJobId) {
			throw new InvariantViolationException("job-prerequisite-not-self", "A job cannot require itself.");
		}

		if (IsDescendantOf(dependentJobId, requiredJobId) || IsDescendantOf(requiredJobId, dependentJobId)) {
			throw new InvariantViolationException(
				"job-prerequisite-is-hierarchy-edge",
				"A prerequisite edge cannot connect nodes that are ancestor/descendant of each other.");
		}

		if (_prerequisites.Contains((requiredJobId, dependentJobId))) {
			throw new InvariantViolationException("job-prerequisite-already-exists", "This prerequisite edge already exists.");
		}

		if (WouldCreateCycle(requiredJobId, dependentJobId)) {
			throw new InvariantViolationException("job-prerequisite-would-cycle", "This prerequisite edge would create a cycle.");
		}

		_prerequisites.Add((requiredJobId, dependentJobId));
	}

	private static IEnumerable<T> ApplyPaging<T>(IEnumerable<T> sequence, int offset, int? limit)
	{
		var skipped = sequence.Skip(offset);
		return limit.HasValue ? skipped.Take(limit.Value) : skipped;
	}

	private static bool MatchesArchiveFilter(JobNodeResult node, JobArchiveFilter archiveFilter) => archiveFilter switch {
		JobArchiveFilter.ActiveOnly => node.ArchivedAt is null,
		JobArchiveFilter.ArchivedOnly => node.ArchivedAt is not null,
		JobArchiveFilter.All => true,
		_ => throw new ArgumentOutOfRangeException(nameof(archiveFilter), archiveFilter, null),
	};

	private JobNodeSummaryResult ToSummary(JobNodeResult node)
	{
		var enriched = WithStructuralFacts(node);
		return new() {
			Id = enriched.Id,
			ParentId = enriched.ParentId,
			Kind = enriched.Kind,
			Description = enriched.Description,
			OwnerUserId = enriched.OwnerUserId,
			Priority = enriched.Priority,
			ArchivedAt = enriched.ArchivedAt,
			HasChildren = enriched.HasChildren,
			HasLeafWork = enriched.HasLeafWork,
		};
	}

	private bool WouldCreateCycle(JobNodeId requiredJobId, JobNodeId dependentJobId)
	{
		var visited = new HashSet<JobNodeId>();
		var stack = new Stack<JobNodeId>();
		stack.Push(dependentJobId);

		while (stack.Count > 0) {
			var current = stack.Pop();
			if (current == requiredJobId) {
				return true;
			}

			if (!visited.Add(current)) {
				continue;
			}

			foreach (var edge in _prerequisites) {
				if (edge.RequiredJobId == current) {
					stack.Push(edge.DependentJobId);
				}
			}
		}

		return false;
	}

	private JobNodeResult Create(CreateJobNodeRequest request)
	{
		_ = GetExisting(request.ParentId);
		AuthorizeOrThrow(request.Context.Actor, request.ParentId);

		var node = WithStructuralFacts(new() {
			Id = new(_nextId++),
			ParentId = request.ParentId,
			Kind = NodeKind.Leaf,
			Description = request.Description,
			WriteUp = request.WriteUp,
			PostedByUserId = request.Context.Actor,
			OwnerUserId = request.OwnerUserId,
			ExpectedDurationHours = request.ExpectedDurationHours,
			ExpectedCost = request.ExpectedCost,
			NeededStart = request.NeededStart,
			NeededFinish = request.NeededFinish,
			Priority = request.Priority,
			PostedAt = NowToReturn,
			Version = 1,
			HasChildren = false,
			HasLeafWork = false,
		});
		_nodes[node.Id] = node;
		if (node.ParentId is JobNodeId parentId && _nodes.TryGetValue(parentId, out var parent)) {
			_nodes[parentId] = WithStructuralFacts(parent);
		}

		return node;
	}

	private static NodeKind DeriveKind(JobNodeId? parentId, bool hasChildren)
	{
		if (parentId is null) {
			return NodeKind.Root;
		}

		if (hasChildren) {
			return NodeKind.Branch;
		}

		return NodeKind.Leaf;
	}

	private JobNodeResult WithStructuralFacts(JobNodeResult node)
	{
		var hasChildren = _nodes.Values.Any(n => n.ParentId == node.Id);
		var hasLeafWork = _leafWork.ContainsKey(node.Id);
		return node with { HasChildren = hasChildren, HasLeafWork = hasLeafWork, Kind = DeriveKind(node.ParentId, hasChildren) };
	}

	private void RefreshStructuralFacts(JobNodeId nodeId)
	{
		if (!_nodes.TryGetValue(nodeId, out var node)) {
			return;
		}

		_nodes[nodeId] = WithStructuralFacts(node);
	}

	private void AuthorizeOrThrow(AppUserId actorId, JobNodeId nodeId)
	{
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];

		if (!JobNodeAccessPolicy.CanManage(roles, OwnsNodeOrAncestor(actorId, nodeId))) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage job node {nodeId}.");
		}
	}

	public bool OwnsNodeOrAncestor(AppUserId actorId, JobNodeId? nodeId)
	{
		var current = nodeId;
		while (current is JobNodeId id) {
			var node = GetExisting(id);
			if (node.OwnerUserId == actorId) {
				return true;
			}

			current = node.ParentId;
		}

		return false;
	}

	private bool IsDescendantOf(JobNodeId candidate, JobNodeId ancestor)
	{
		var current = GetExisting(candidate).ParentId;
		while (current is JobNodeId id) {
			if (id == ancestor) {
				return true;
			}

			current = GetExisting(id).ParentId;
		}

		return false;
	}

	private static void CheckVersionOrThrow(long currentVersion, long expectedVersion)
	{
		if (currentVersion != expectedVersion) {
			throw new ConcurrencyConflictException(
				$"Expected version {expectedVersion} but the current version is {currentVersion}.");
		}
	}

	private JobNodeResult GetExisting(JobNodeId id) =>
		_nodes.TryGetValue(id, out var node) ? node : throw new EntityNotFoundException($"Job node {id} does not exist.");
}
