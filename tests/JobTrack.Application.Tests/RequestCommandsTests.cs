namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using NodaTime;

public sealed class RequestCommandsTests
{
	private static readonly AppUserId RequesterId = new(10);
	private static readonly Instant Now = Instant.FromUtc(2026, 1, 2, 12, 0);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static RequestCommands CreateSut(FakeJobRequestCommandPort port) =>
		new(port, new FakeRequesterDurationQueries(new Dictionary<JobNodeId, AllocatedDuration>()), new FixedClock(Now));

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
		var sut = CreateSut(new FakeJobRequestCommandPort());

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
		var sut = CreateSut(new FakeJobRequestCommandPort());

		var act = () => sut.MoveAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
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
		var sut = new RequestCommands(requestPort, durationQueries, new FixedClock(Now));

		var result = await sut.GetDetailAsync(new() { Context = ContextFor(RequesterId), NodeId = rootId });

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
		var sut = new RequestCommands(requestPort, durationQueries, new FixedClock(Now));

		var act = () => sut.GetDetailAsync(new() { Context = ContextFor(RequesterId), NodeId = new(100) });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		durationQueries.CallCount.Should().Be(0);
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
