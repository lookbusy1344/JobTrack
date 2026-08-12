namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using NodaTime;
using TestSupport;

/// <summary>
///     Application-slice coverage of <see cref="IJobQueries.GetConcurrentWorkAsync" /> over fake ports
///     (plan §7.3): admission, node existence, the described-node join, and clipping of an unfinished
///     session. The overlap arithmetic itself belongs to
///     <c>ConcurrentWorkCalculatorTests</c> and is not restated here.
/// </summary>
public sealed class JobQueriesConcurrentWorkTests
{
	private static readonly AppUserId ActorId = new(1);
	private static readonly AppUserId WorkerId = new(2);
	private static readonly JobNodeId SubjectId = new(10);
	private static readonly JobNodeId OtherId = new(20);
	private static readonly Instant Now = Instant.FromUtc(2026, 3, 2, 18, 0);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	private static Instant At(int hour) => Instant.FromUtc(2026, 3, 2, hour, 0);

	private static JobQueries CreateSut(
		FakeJobNodeCommandPort nodePort, FakeWorkSessionQueryPort sessionPort, params EmployeeRole[] actorRoles)
	{
		var employeeQueryPort = FakeEmployeeQueryPort.AllowingAnyActor();
		employeeQueryPort.SeedRoles(ActorId, actorRoles.Length == 0 ? [EmployeeRole.Worker] : [.. actorRoles]);

		return new(employeeQueryPort, nodePort, nodePort, nodePort, sessionPort, new FakeLeafWorkQueryPort(),
			new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(), new FakeRateQueryPort(), new FakeCostQueries(),
			new AdjustableClock(Now));
	}

	private static FakeJobNodeCommandPort NodePortWith(params JobNodeId[] nodeIds)
	{
		var port = new FakeJobNodeCommandPort();
		foreach (var nodeId in nodeIds) {
			port.SeedNode(new() {
				Id = nodeId,
				ParentId = null,
				Kind = NodeKind.Leaf,
				Description = $"Job {nodeId.Value}",
				PostedByUserId = ActorId,
				OwnerUserId = WorkerId,
				Priority = Priority.Medium,
				PostedAt = port.NowToReturn,
				HasChildren = false,
				HasLeafWork = true,
				Version = 1,
			});
		}

		return port;
	}

	private static void SeedSession(
		FakeWorkSessionQueryPort port, long id, JobNodeId nodeId, AppUserId worker, int startHour, int? finishHour)
	{
		port.SeedSession(new() {
			Id = new(id),
			LeafWorkId = nodeId,
			WorkedByUserId = worker,
			StartedAt = At(startHour),
			FinishedAt = finishHour is int hour ? At(hour) : null,
			ChangedAt = At(startHour),
			Version = 1,
		});
	}

	[Fact]
	public async Task A_job_whose_workers_worked_nothing_else_reports_no_concurrent_work()
	{
		var sessionPort = new FakeWorkSessionQueryPort();
		SeedSession(sessionPort, 1, SubjectId, WorkerId, 9, 12);
		var sut = CreateSut(NodePortWith(SubjectId), sessionPort);

		var result = await sut.GetConcurrentWorkAsync(new() {
			Context = ContextFor(ActorId),
			NodeId = SubjectId,
		});

		result.Rows.Should().BeEmpty();
		result.IsTruncated.Should().BeFalse();
		result.NodeId.Should().Be(SubjectId);
	}

	[Fact]
	public async Task A_concurrent_job_is_reported_with_its_own_description_and_overlap()
	{
		var sessionPort = new FakeWorkSessionQueryPort();
		SeedSession(sessionPort, 1, SubjectId, WorkerId, 9, 12);
		SeedSession(sessionPort, 2, OtherId, WorkerId, 11, 13);
		var sut = CreateSut(NodePortWith(SubjectId, OtherId), sessionPort);

		var result = await sut.GetConcurrentWorkAsync(new() {
			Context = ContextFor(ActorId),
			NodeId = SubjectId,
		});

		var row = result.Rows.Should().ContainSingle().Subject;
		row.WorkedByUserId.Should().Be(WorkerId);
		row.Node.Id.Should().Be(OtherId);
		row.Node.Description.Should().Be("Job 20");
		row.TotalOverlap.Should().Be(Duration.FromHours(1));
		row.OverlapCount.Should().Be(1);
		row.FirstOverlapStart.Should().Be(At(11));
		row.LastOverlapEnd.Should().Be(At(12));
	}

	[Fact]
	public async Task An_unfinished_session_counts_up_to_the_as_of_instant()
	{
		var sessionPort = new FakeWorkSessionQueryPort();
		SeedSession(sessionPort, 1, SubjectId, WorkerId, 9, null);
		SeedSession(sessionPort, 2, OtherId, WorkerId, 10, null);
		var sut = CreateSut(NodePortWith(SubjectId, OtherId), sessionPort);

		var result = await sut.GetConcurrentWorkAsync(
			new() {
				Context = ContextFor(ActorId),
				NodeId = SubjectId,
				AsOf = At(12),
			});

		result.AsOf.Should().Be(At(12));
		result.Rows.Should().ContainSingle().Which.TotalOverlap.Should().Be(Duration.FromHours(2));
	}

	[Fact]
	public async Task An_omitted_as_of_bounds_unfinished_sessions_at_the_current_instant()
	{
		var sessionPort = new FakeWorkSessionQueryPort();
		SeedSession(sessionPort, 1, SubjectId, WorkerId, 9, null);
		SeedSession(sessionPort, 2, OtherId, WorkerId, 17, null);
		var sut = CreateSut(NodePortWith(SubjectId, OtherId), sessionPort);

		var result = await sut.GetConcurrentWorkAsync(new() {
			Context = ContextFor(ActorId),
			NodeId = SubjectId,
		});

		result.AsOf.Should().Be(Now);
		result.Rows.Should().ContainSingle().Which.TotalOverlap.Should().Be(Duration.FromHours(1));
	}

	[Fact]
	public async Task A_concurrent_job_that_no_longer_resolves_is_left_out_rather_than_reported_as_a_bare_id()
	{
		var sessionPort = new FakeWorkSessionQueryPort();
		SeedSession(sessionPort, 1, SubjectId, WorkerId, 9, 12);
		SeedSession(sessionPort, 2, OtherId, WorkerId, 11, 13);
		var sut = CreateSut(NodePortWith(SubjectId), sessionPort);

		var result = await sut.GetConcurrentWorkAsync(new() {
			Context = ContextFor(ActorId),
			NodeId = SubjectId,
		});

		result.Rows.Should().BeEmpty();
	}

	[Fact]
	public async Task A_nonexistent_job_throws()
	{
		var sut = CreateSut(NodePortWith(SubjectId), new());

		var act = () => sut.GetConcurrentWorkAsync(new() {
			Context = ContextFor(ActorId),
			NodeId = new(9999),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_requester_may_not_read_concurrent_work()
	{
		var sut = CreateSut(NodePortWith(SubjectId), new(), EmployeeRole.Requester);

		var act = () => sut.GetConcurrentWorkAsync(new() {
			Context = ContextFor(ActorId),
			NodeId = SubjectId,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_null_request_is_rejected()
	{
		var sut = CreateSut(NodePortWith(SubjectId), new());

		var act = () => sut.GetConcurrentWorkAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
