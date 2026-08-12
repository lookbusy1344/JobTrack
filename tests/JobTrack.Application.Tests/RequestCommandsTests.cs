namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using Domain.Hierarchy;
using NodaTime;
using Ports;

public sealed class RequestCommandsTests
{
	private static readonly AppUserId RequesterId = new(10);
	private static readonly Instant Now = Instant.FromUtc(2026, 1, 2, 12, 0);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	private static RequestCommands CreateSut(FakeJobRequestCommandPort port) =>
		new(port, new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration>()),
			new FakeReadinessQueryPort(), new FixedClock(Now));

	[Fact]
	public async Task SubmitAsync_delegates_to_the_port()
	{
		var port = new FakeJobRequestCommandPort();
		var sut = CreateSut(port);

		var result = await sut.SubmitAsync(new() {
			Context = ContextFor(RequesterId),
			HoldingAreaId = new(1),
			Description = "Please schedule this work.",
		});

		port.LastSubmitRequest.Should().NotBeNull();
		port.LastSubmitRequest!.Context.Actor.Should().Be(RequesterId);
		result.Description.Should().Be("Please schedule this work.");
	}

	[Fact]
	public async Task SubmitAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new());

		var act = () => sut.SubmitAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	/// <summary>
	///     Staff triage moves a requester job to a new parent (plan §5, §9 Stage 5) without
	///     altering its <c>job_request</c> anchor — the anchor itself is preserved by the persistence
	///     port keying on <see cref="JobNodeId" />, not the parent (TC-DB-REQ-003/-005); this pins the
	///     facade passes the move through unchanged.
	/// </summary>
	[Fact]
	public async Task MoveAsync_delegates_to_the_port_unchanged()
	{
		var jobManagerId = new AppUserId(20);
		var port = new FakeJobRequestCommandPort();
		var sut = CreateSut(port);
		var nodeId = new JobNodeId(100);
		var newParentId = new JobNodeId(200);

		var result = await sut.MoveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = nodeId,
			NewParentId = newParentId,
			Version = 1,
		});

		port.LastMoveRequest.Should().NotBeNull();
		port.LastMoveRequest!.NodeId.Should().Be(nodeId);
		port.LastMoveRequest!.NewParentId.Should().Be(newParentId);
		result.Id.Should().Be(nodeId);
		result.ParentId.Should().Be(newParentId);
	}

	[Fact]
	public async Task MoveAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new());

		var act = () => sut.MoveAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetMyRequestsAsync_adds_readiness_to_every_summary_from_one_batched_projection()
	{
		var readyId = new JobNodeId(100);
		var blockedId = new JobNodeId(200);
		var requiredId = new JobNodeId(300);
		var requestPort = new FakeJobRequestCommandPort {
			SummaryResults = [Summary(readyId), Summary(blockedId)],
		};
		var readinessPort = new FakeReadinessQueryPort {
			NodesById = new() {
				[readyId] = new(readyId, null, [], null),
				[blockedId] = new(blockedId, null, [], null),
				[requiredId] = new(requiredId, null, [], Achievement.Waiting),
			},
			Prerequisites = [new(requiredId, blockedId)],
		};
		var sut = new RequestCommands(
			requestPort, new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration>()), readinessPort, new FixedClock(Now));

		var result = await sut.GetMyRequestsAsync(ContextFor(RequesterId));

		result.Single(summary => summary.JobNodeId == readyId).IsReady.Should().BeTrue();
		result.Single(summary => summary.JobNodeId == blockedId).IsReady.Should().BeFalse();
		readinessPort.BatchCallCount.Should().Be(1, "list readiness should be materialized once, not queried per request");
		readinessPort.LastNodeIds.Should().BeEquivalentTo([readyId, blockedId]);
	}

	[Fact]
	public async Task GetDetailAsync_adds_requester_visible_allocated_duration_to_every_subtree_node()
	{
		var rootId = new JobNodeId(100);
		var leafId = new JobNodeId(101);
		var requestPort = new FakeJobRequestCommandPort {
			DetailResult = new() {
				JobNodeId = rootId,
				RequesterUserId = RequesterId,
				RequesterDisplayName = "Client Requester",
				RequesterUserName = "requester",
				Description = "Repair printer",
				Status = RequesterStatus.InProgress,
				Kind = NodeKind.Branch,
				SubtreeAchievement = BranchAchievement.Unfinished,
				SubmittedAt = Now.Minus(Duration.FromDays(1)),
				AcknowledgedAt = Now.Minus(Duration.FromHours(12)),
				Version = 1,
				Subtree = [
					new() {
						JobNodeId = rootId,
						Description = "Repair printer",
						Status = RequesterStatus.InProgress,
						ParentId = null,
						LastUpdatedAt = Now,
					},
					new() {
						JobNodeId = leafId,
						Description = "Replace feed roller",
						Status = RequesterStatus.InProgress,
						ParentId = rootId,
						LastUpdatedAt = Now,
					},
				],
				Notes = [],
			},
		};
		var rootDuration = AllocatedDuration.FromShare(new(Duration.FromHours(2).BclCompatibleTicks, 1));
		var leafDuration = AllocatedDuration.FromShare(new(Duration.FromHours(2).BclCompatibleTicks, 1));
		var durationQueries = new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration> {
			[rootId] = rootDuration,
			[leafId] = leafDuration,
		});
		var sut = new RequestCommands(requestPort, durationQueries, new FakeReadinessQueryPort(), new FixedClock(Now));

		var result = await sut.GetDetailAsync(new() {
			Context = ContextFor(RequesterId),
			NodeId = rootId,
		});

		result.Subtree.Single(node => node.JobNodeId == rootId).AllocatedDuration.Should().Be(rootDuration);
		result.Subtree.Single(node => node.JobNodeId == leafId).AllocatedDuration.Should().Be(leafDuration);
		durationQueries.LastNodeId.Should().Be(rootId);
		durationQueries.LastAsOf.Should().Be(Now);
	}

	[Fact]
	public async Task GetDetailAsync_does_not_query_work_duration_until_request_access_is_authorized()
	{
		var requestPort = new FakeJobRequestCommandPort {
			GetDetailException = new AuthorizationDeniedException("Not this request."),
		};
		var durationQueries = new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration>());
		var sut = new RequestCommands(requestPort, durationQueries, new FakeReadinessQueryPort(), new FixedClock(Now));

		var act = () => sut.GetDetailAsync(new() {
			Context = ContextFor(RequesterId),
			NodeId = new(100),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		durationQueries.CallCount.Should().Be(0);
	}

	/// <summary>
	///     Readiness is spec §6's aggregate over the node's own and its ancestors' prerequisites, which
	///     the requester-safe port cannot see — so <see cref="RequestCommands" /> composes it from the
	///     readiness port, exactly as it does ADR 0054's allocated duration.
	/// </summary>
	[Fact]
	public async Task GetDetailAsync_reports_a_request_blocked_by_an_unsatisfied_prerequisite_as_not_ready()
	{
		var anchorId = new JobNodeId(100);
		var blockerId = new JobNodeId(200);
		var requestPort = new FakeJobRequestCommandPort {
			DetailResult = DetailFor(anchorId),
		};
		var readiness = new FakeReadinessQueryPort {
			NodesById = new() {
				[anchorId] = new(anchorId, null, [], Achievement.InProgress),
				[blockerId] = new(blockerId, null, [], Achievement.InProgress),
			},
			Prerequisites = [new(blockerId, anchorId)],
		};
		var sut = new RequestCommands(
			requestPort, new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration>()), readiness, new FixedClock(Now));

		var result = await sut.GetDetailAsync(new() {
			Context = ContextFor(RequesterId),
			NodeId = anchorId,
		});

		result.IsReady.Should().BeFalse();
		readiness.LastNodeId.Should().Be(anchorId);
	}

	[Fact]
	public async Task GetDetailAsync_reports_a_request_whose_prerequisite_succeeded_as_ready()
	{
		var anchorId = new JobNodeId(100);
		var blockerId = new JobNodeId(200);
		var requestPort = new FakeJobRequestCommandPort {
			DetailResult = DetailFor(anchorId),
		};
		var readiness = new FakeReadinessQueryPort {
			NodesById = new() {
				[anchorId] = new(anchorId, null, [], Achievement.InProgress),
				[blockerId] = new(blockerId, null, [], Achievement.Success),
			},
			Prerequisites = [new(blockerId, anchorId)],
		};
		var sut = new RequestCommands(
			requestPort, new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration>()), readiness, new FixedClock(Now));

		var result = await sut.GetDetailAsync(new() {
			Context = ContextFor(RequesterId),
			NodeId = anchorId,
		});

		result.IsReady.Should().BeTrue();
	}

	/// <summary>
	///     The anchor's structural kind and both achievement facts are the port's to determine (it holds
	///     the subtree the rollup is derived from); the facade must pass them through untouched.
	/// </summary>
	[Fact]
	public async Task GetDetailAsync_passes_the_ports_kind_and_achievements_through_unchanged()
	{
		var anchorId = new JobNodeId(100);
		var requestPort = new FakeJobRequestCommandPort {
			DetailResult = DetailFor(anchorId) with {
				Kind = NodeKind.Leaf,
				SubtreeAchievement = BranchAchievement.Unfinished,
				LeafAchievement = Achievement.InProgress,
			},
		};
		var sut = CreateSut(requestPort);

		var result = await sut.GetDetailAsync(new() {
			Context = ContextFor(RequesterId),
			NodeId = anchorId,
		});

		result.Kind.Should().Be(NodeKind.Leaf);
		result.SubtreeAchievement.Should().Be(BranchAchievement.Unfinished);
		result.LeafAchievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task GetDetailAsync_does_not_query_readiness_until_request_access_is_authorized()
	{
		var requestPort = new FakeJobRequestCommandPort {
			GetDetailException = new AuthorizationDeniedException("Not this request."),
		};
		var readiness = new FakeReadinessQueryPort();
		var sut = new RequestCommands(
			requestPort, new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration>()), readiness, new FixedClock(Now));

		var act = () => sut.GetDetailAsync(new() {
			Context = ContextFor(RequesterId),
			NodeId = new(100),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		readiness.CallCount.Should().Be(0);
	}

	private static JobRequestDetailResult DetailFor(JobNodeId anchorId) => new() {
		JobNodeId = anchorId,
		RequesterUserId = RequesterId,
		RequesterDisplayName = "Client Requester",
		RequesterUserName = "requester",
		Description = "Repair printer",
		Status = RequesterStatus.InProgress,
		Kind = NodeKind.Leaf,
		SubtreeAchievement = BranchAchievement.Unfinished,
		LeafAchievement = Achievement.InProgress,
		SubmittedAt = Now.Minus(Duration.FromDays(1)),
		AcknowledgedAt = Now.Minus(Duration.FromHours(12)),
		Version = 1,
		Subtree = [
			new() {
				JobNodeId = anchorId,
				Description = "Repair printer",
				Status = RequesterStatus.InProgress,
				ParentId = null,
				LastUpdatedAt = Now,
			},
		],
		Notes = [],
	};

	private static JobRequestSummaryResult Summary(JobNodeId nodeId) => new() {
		JobNodeId = nodeId,
		Description = $"Request {nodeId.Value}",
		Status = RequesterStatus.Submitted,
		SubmittedAt = Now,
		Version = 1,
	};

	private sealed class FakeReadinessQueryPort : IReadinessQueryPort
	{
		public Dictionary<JobNodeId, HierarchyNode> NodesById { get; init; } = [];

		public EquatableArray<PrerequisiteEdge> Prerequisites { get; init; } = [];

		public int CallCount { get; private set; }

		public int BatchCallCount { get; private set; }

		public JobNodeId? LastNodeId { get; private set; }

		public IReadOnlyCollection<JobNodeId>? LastNodeIds { get; private set; }

		public Task<ReadinessQueryResult> GetReadinessInputsAsync(JobNodeId nodeId, CancellationToken cancellationToken = default)
		{
			++CallCount;
			LastNodeId = nodeId;

			var nodes = NodesById.Count > 0
				? NodesById
				: new() {
					[nodeId] = new(nodeId, null, [], null),
				};

			return Task.FromResult<ReadinessQueryResult>(new() {
				NodesById = EquatableDictionaryFactory.CopyOf(nodes),
				Prerequisites = Prerequisites,
			});
		}

		public Task<ReadinessQueryResult> GetReadinessInputsForNodesAsync(
			IReadOnlyCollection<JobNodeId> nodeIds, CancellationToken cancellationToken = default)
		{
			++BatchCallCount;
			LastNodeIds = nodeIds;
			var nodes = NodesById.Count > 0
				? NodesById
				: nodeIds.ToDictionary(nodeId => nodeId, nodeId => new HierarchyNode(nodeId, null, [], null));

			return Task.FromResult<ReadinessQueryResult>(new() {
				NodesById = EquatableDictionaryFactory.CopyOf(nodes),
				Prerequisites = Prerequisites,
			});
		}
	}

	private sealed class FakeRequesterDurationQueries(
		IReadOnlyDictionary<JobNodeId, AllocatedDuration> durations) : IRequesterDurationQueries
	{
		public int CallCount { get; private set; }

		public Instant? LastAsOf { get; private set; }

		public JobNodeId? LastNodeId { get; private set; }

		public Task<EquatableDictionary<JobNodeId, AllocatedDuration>> GetRequesterVisibleHierarchyAsync(
			JobNodeId nodeId, Instant asOf, CancellationToken cancellationToken = default)
		{
			++CallCount;
			LastNodeId = nodeId;
			LastAsOf = asOf;
			return Task.FromResult(EquatableDictionaryFactory.CopyOf(durations));
		}
	}

	private sealed class FixedClock(Instant now) : IClock
	{
		public Instant GetCurrentInstant() => now;
	}
}
