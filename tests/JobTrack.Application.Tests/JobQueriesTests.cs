namespace JobTrack.Application.Tests;

using System.Diagnostics;
using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;
using NodaTime;
using TestSupport;

public sealed class JobQueriesTests
{
	private static readonly AppUserId AdministratorId = new(1);
	private static readonly AppUserId WorkerId = new(2);
	private static readonly AppUserId OtherWorkerId = new(3);

	private static FakeEmployeeQueryPort CreateSeededPort()
	{
		var port = new FakeEmployeeQueryPort();

		port.Seed(
			AdministratorId,
			new() {
				Id = AdministratorId,
				DisplayName = "Ada Lovelace",
				IanaTimeZone = "Europe/London",
				Version = 1,
			},
			new() {
				Id = AdministratorId,
				UserName = "ada",
				IsEnabled = true,
				RequiresPasswordChange = false,
				Roles = [EmployeeRole.Administrator],
			},
			[EmployeeRole.Administrator]);

		port.Seed(
			WorkerId,
			new() {
				Id = WorkerId,
				DisplayName = "Grace Hopper",
				IanaTimeZone = "America/New_York",
				Version = 1,
			},
			new() {
				Id = WorkerId,
				UserName = "grace",
				IsEnabled = true,
				RequiresPasswordChange = false,
				Roles = [EmployeeRole.Worker],
			},
			[EmployeeRole.Worker]);

		port.Seed(
			OtherWorkerId,
			new() {
				Id = OtherWorkerId,
				DisplayName = "Alan Turing",
				IanaTimeZone = "Europe/London",
				Version = 1,
			},
			new() {
				Id = OtherWorkerId,
				UserName = "alan",
				IsEnabled = true,
				RequiresPasswordChange = false,
				Roles = [EmployeeRole.Worker],
			},
			[EmployeeRole.Worker]);

		return port;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static JobQueries CreateSut(FakeEmployeeQueryPort employeeQueryPort) =>
		new(employeeQueryPort, new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

	private static JobQueries CreateSut(FakeJobNodeCommandPort browseReadinessAndAwaitingProgressQueryPort) =>
		new(new FakeEmployeeQueryPort(), browseReadinessAndAwaitingProgressQueryPort, browseReadinessAndAwaitingProgressQueryPort,
			browseReadinessAndAwaitingProgressQueryPort, new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(),
			new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(), new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

	private static JobQueries CreateSut(FakeWorkSessionQueryPort workSessionQueryPort) =>
		new(new FakeEmployeeQueryPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			workSessionQueryPort, new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

	private static JobQueries CreateSut(FakeLeafWorkQueryPort leafWorkQueryPort) =>
		new(new FakeEmployeeQueryPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), leafWorkQueryPort, new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

	private static JobQueries CreateSut(FakePrerequisiteQueryPort prerequisiteQueryPort) =>
		new(new FakeEmployeeQueryPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), prerequisiteQueryPort, new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

	private static JobQueries CreateSut(FakeScheduleQueryPort scheduleQueryPort) =>
		new(new FakeEmployeeQueryPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), scheduleQueryPort,
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

	private static JobQueries CreateSut(FakeRateQueryPort rateQueryPort) =>
		new(new FakeEmployeeQueryPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			rateQueryPort, new FakeCostQueries(), SystemClock.Instance);

	private static JobQueries CreateSut(FakeJobNodeCommandPort browseReadinessAndAwaitingProgressQueryPort, FakeCostQueries costQueries) =>
		new(EmployeePortMirroring(browseReadinessAndAwaitingProgressQueryPort), browseReadinessAndAwaitingProgressQueryPort,
			browseReadinessAndAwaitingProgressQueryPort,
			browseReadinessAndAwaitingProgressQueryPort, new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(),
			new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(), new FakeRateQueryPort(), costQueries, SystemClock.Instance);

	/// <summary>
	///     An employee port seeded with the same roles as <paramref name="source" />. The per-node cost
	///     filter (ADR 0042) resolves the actor's roles through <c>IEmployeeQueryPort</c>, which in
	///     production reads the same database as every other port — so the fakes must agree rather than
	///     leaving the actor unknown to one of them.
	/// </summary>
	private static FakeEmployeeQueryPort EmployeePortMirroring(FakeJobNodeCommandPort source)
	{
		var employeeQueryPort = new FakeEmployeeQueryPort();
		foreach (var (actorId, roles) in source.SeededRoles) {
			employeeQueryPort.SeedRoles(actorId, roles);
		}

		return employeeQueryPort;
	}

	private static JobQueries CreateSut(FakeCostQueries costQueries) =>
		new(new FakeEmployeeQueryPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), costQueries, SystemClock.Instance);

	[Fact]
	public async Task An_employee_can_view_their_own_profile()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.GetEmployeeProfileAsync(
			new() { Context = ContextFor(WorkerId), TargetUserId = WorkerId });

		result.DisplayName.Should().Be("Grace Hopper");
	}

	[Fact]
	public async Task An_administrator_can_view_another_employees_profile()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.GetEmployeeProfileAsync(
			new() { Context = ContextFor(AdministratorId), TargetUserId = WorkerId });

		result.DisplayName.Should().Be("Grace Hopper");
	}

	[Fact]
	public async Task Getting_an_employee_profile_emits_one_bounded_activity_with_actor_and_target_tags()
	{
		var stopped = new List<Activity>();
		using var listener = new ActivityListener {
			ShouldListenTo = source => source.Name == JobTrackDiagnostics.ActivitySourceName,
			Sample = static (ref _) => ActivitySamplingResult.AllData,
			ActivityStopped = stopped.Add,
		};
		ActivitySource.AddActivityListener(listener);
		var sut = CreateSut(CreateSeededPort());
		var request = new GetEmployeeProfileRequest { Context = ContextFor(AdministratorId), TargetUserId = WorkerId };

		_ = await sut.GetEmployeeProfileAsync(request);

		var operation = stopped.Should()
			.ContainSingle(activity => activity.OperationName == "query.get-employee-profile")
			.Which;
		operation.Status.Should().Be(ActivityStatusCode.Ok);
		operation.GetTagItem("jobtrack.actor_id").Should().Be(AdministratorId.Value);
		operation.GetTagItem("jobtrack.correlation_id").Should().Be(request.Context.CorrelationId.ToString("D"));
		operation.GetTagItem("jobtrack.target.user_id").Should().Be(WorkerId.Value);
		operation.GetTagItem("jobtrack.display_name").Should().BeNull();
	}

	[Fact]
	public async Task Getting_the_employee_directory_emits_actor_and_correlation_tags()
	{
		var stopped = new List<Activity>();
		using var listener = new ActivityListener {
			ShouldListenTo = source => source.Name == JobTrackDiagnostics.ActivitySourceName,
			Sample = static (ref _) => ActivitySamplingResult.AllData,
			ActivityStopped = stopped.Add,
		};
		ActivitySource.AddActivityListener(listener);
		var sut = CreateSut(CreateSeededPort());
		var request = new GetEmployeeDirectoryRequest { Context = ContextFor(AdministratorId) };

		_ = await sut.GetEmployeeDirectoryAsync(request);

		var operation = stopped.Should()
			.ContainSingle(activity => activity.OperationName == "query.get-employee-directory")
			.Which;
		operation.Status.Should().Be(ActivityStatusCode.Ok);
		operation.GetTagItem("jobtrack.actor_id").Should().Be(AdministratorId.Value);
		operation.GetTagItem("jobtrack.correlation_id").Should().Be(request.Context.CorrelationId.ToString("D"));
	}

	[Fact]
	public async Task A_worker_cannot_view_another_workers_profile()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);

		var act = () => sut.GetEmployeeProfileAsync(
			new() { Context = ContextFor(WorkerId), TargetUserId = OtherWorkerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		port.GetActorRolesCallCount.Should().Be(1);
		port.GetEmployeeProfileCallCount.Should().Be(0);
	}

	[Fact]
	public async Task An_employee_can_view_their_own_account_state()
	{
		var sut = CreateSut(CreateSeededPort());

		var result = await sut.GetAccountStateAsync(
			new() { Context = ContextFor(WorkerId), TargetUserId = WorkerId });

		result.UserName.Should().Be("grace");
		result.Roles.Should().Equal(EmployeeRole.Worker);
	}

	[Fact]
	public async Task A_worker_cannot_view_another_workers_account_state()
	{
		var port = CreateSeededPort();
		var sut = CreateSut(port);

		var act = () => sut.GetAccountStateAsync(
			new() { Context = ContextFor(WorkerId), TargetUserId = OtherWorkerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		port.GetActorRolesCallCount.Should().Be(1);
		port.GetAccountStateCallCount.Should().Be(0);
	}

	[Fact]
	public async Task Querying_a_nonexistent_employee_throws_not_found()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.GetEmployeeProfileAsync(
			new() { Context = ContextFor(AdministratorId), TargetUserId = new(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_employee_query_port()
	{
		var act = () => new JobQueries(
			null!, new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_readiness_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), null!, new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_browse_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), null!, new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_awaiting_progress_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), null!,
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_work_session_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			null!, new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_leaf_work_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), null!, new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_prerequisite_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), null!, new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_schedule_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), null!,
			new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_rate_query_port()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			null!, new FakeCostQueries(), SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_cost_queries()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), null!, SystemClock.Instance);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_clock()
	{
		var act = () => new JobQueries(
			CreateSeededPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(), new FakeJobNodeCommandPort(),
			new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(), new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(),
			new FakeRateQueryPort(), new FakeCostQueries(), null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task GetEmployeeProfileAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		Func<Task> act = () => sut.GetEmployeeProfileAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public void GetEmployeeProfileAsync_throws_synchronously_for_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		Action act = () => _ = sut.GetEmployeeProfileAsync(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task GetEmployeeDirectoryAsync_returns_the_ports_directory()
	{
		var port = CreateSeededPort();
		port.SeedDirectory([
			new() { Id = AdministratorId, DisplayName = "Ada Lovelace", UserName = "ada" },
			new() { Id = WorkerId, DisplayName = "Grace Hopper", UserName = "grace" },
		]);
		var sut = CreateSut(port);

		var result = await sut.GetEmployeeDirectoryAsync(new() { Context = ContextFor(AdministratorId) });

		result.Select(entry => entry.Id).Should().BeEquivalentTo([AdministratorId, WorkerId]);
	}

	[Fact]
	public async Task GetEmployeeDirectoryAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.GetEmployeeDirectoryAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetAllEmployeesAsync_returns_the_ports_full_list()
	{
		var port = CreateSeededPort();
		port.SeedAllEmployees([
			new() { Id = AdministratorId, DisplayName = "Ada Lovelace", UserName = "ada" },
			new() { Id = OtherWorkerId, DisplayName = "Alan Turing", UserName = "alan" },
		]);
		var sut = CreateSut(port);

		var result = await sut.GetAllEmployeesAsync(new() { Context = ContextFor(AdministratorId) });

		result.Select(entry => entry.Id).Should().BeEquivalentTo([AdministratorId, OtherWorkerId]);
	}

	[Fact]
	public async Task GetAllEmployeesAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.GetAllEmployeesAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetAccountStateAsync_rejects_a_null_request()
	{
		var sut = CreateSut(CreateSeededPort());

		var act = () => sut.GetAccountStateAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_leaf_with_no_prerequisites_is_ready()
	{
		var actor = new AppUserId(10);
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(actor, EmployeeRole.Administrator);
		var leaf = new JobNodeId(1);
		port.SeedNode(new() {
			Id = leaf,
			ParentId = null,
			Kind = NodeKind.Leaf,
			Description = "Leaf",
			PostedByUserId = actor,
			OwnerUserId = actor,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetReadinessAsync(
			new() { Context = ContextFor(actor), NodeId = leaf });

		result.IsReady.Should().BeTrue();
		result.Blockers.Should().BeEmpty();
	}

	[Fact]
	public async Task A_leaf_is_not_ready_while_its_prerequisite_has_not_succeeded()
	{
		var actor = new AppUserId(10);
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(actor, EmployeeRole.Administrator);
		var rootId = new JobNodeId(100);
		var branchId = new JobNodeId(10);
		var required = new JobNodeId(1);
		var leaf = new JobNodeId(2);
		port.SeedNode(new() {
			Id = rootId,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = actor,
			OwnerUserId = actor,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = branchId,
			ParentId = rootId,
			Kind = NodeKind.Branch,
			Description = "Branch",
			PostedByUserId = actor,
			OwnerUserId = actor,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = required,
			ParentId = branchId,
			Kind = NodeKind.Leaf,
			Description = "Required",
			PostedByUserId = actor,
			OwnerUserId = actor,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = leaf,
			ParentId = branchId,
			Kind = NodeKind.Leaf,
			Description = "Leaf",
			PostedByUserId = actor,
			OwnerUserId = actor,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		await port.AttachLeafWorkAsync(new() { Context = ContextFor(actor), JobNodeId = required });
		await port.AddPrerequisiteAsync(
			new() { Context = ContextFor(actor), RequiredJobId = required, DependentJobId = leaf });
		var sut = CreateSut(port);

		var result = await sut.GetReadinessAsync(
			new() { Context = ContextFor(actor), NodeId = leaf });

		result.IsReady.Should().BeFalse();
		result.Blockers.Should().ContainSingle(blocker => blocker.RequiredJobId == required && blocker.DeclaredOnJobId == leaf);
	}

	[Fact]
	public async Task GetReadinessAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetReadinessAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task Querying_readiness_of_a_nonexistent_node_throws_not_found()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetReadinessAsync(
			new() { Context = ContextFor(new(10)), NodeId = new(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	private static FakeJobNodeCommandPort CreateSeededTree(AppUserId owner, AppUserId otherOwner, out JobNodeId rootId, out JobNodeId branchId,
		out JobNodeId leafId)
	{
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(owner, EmployeeRole.Administrator);
		rootId = new(1);
		branchId = new(2);
		leafId = new(3);
		var archivedLeafId = new JobNodeId(4);

		port.SeedNode(new() {
			Id = rootId,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = branchId,
			ParentId = rootId,
			Kind = NodeKind.Branch,
			Description = "Kitchen renovation",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = leafId,
			ParentId = branchId,
			Kind = NodeKind.Leaf,
			Description = "Install cabinets",
			PostedByUserId = owner,
			OwnerUserId = otherOwner,
			Priority = Priority.High,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = archivedLeafId,
			ParentId = branchId,
			Kind = NodeKind.Leaf,
			Description = "Old plumbing job",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Low,
			PostedAt = port.NowToReturn,
			ArchivedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});

		return port;
	}

	private static FakeJobNodeCommandPort CreateAwaitingProgressPort(AppUserId owner, int leafCount)
	{
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(owner, EmployeeRole.Administrator);
		var rootId = new JobNodeId(1);
		port.SeedNode(new() {
			Id = rootId,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = true,
			HasLeafWork = false,
			Version = 1,
		});

		for (var index = 0; index < leafCount; index++) {
			var leafId = new JobNodeId(index + 2);
			port.SeedNode(new() {
				Id = leafId,
				ParentId = rootId,
				Kind = NodeKind.Leaf,
				Description = $"Leaf {index:D3}",
				PostedByUserId = owner,
				OwnerUserId = owner,
				Priority = Priority.Medium,
				PostedAt = port.NowToReturn,
				HasChildren = false,
				HasLeafWork = false,
				Version = 1,
			});
		}

		return port;
	}

	private static JobNodeResult SubtreeNode(
		JobNodeId id, JobNodeId? parentId, NodeKind kind, string description, AppUserId owner, Instant postedAt) =>
		new() {
			Id = id,
			ParentId = parentId,
			Kind = kind,
			Description = description,
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = postedAt,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		};

	[Fact]
	public async Task GetJobNodeAsync_with_null_returns_the_root_with_no_ancestors()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out var rootId, out _, out _);
		var sut = CreateSut(port);

		var result = await sut.GetJobNodeAsync(new() { Context = ContextFor(owner), NodeId = null });

		result.Node.Id.Should().Be(rootId);
		result.Ancestors.Should().BeEmpty();
	}

	[Fact]
	public async Task GetJobNodeAsync_returns_a_node_with_root_first_ancestry()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out var rootId, out var branchId, out var leafId);
		var sut = CreateSut(port);

		var result = await sut.GetJobNodeAsync(new() { Context = ContextFor(owner), NodeId = leafId });

		result.Node.Id.Should().Be(leafId);
		result.Ancestors.Select(a => a.Id).Should().ContainInOrder(rootId, branchId);
	}

	[Fact]
	public async Task GetJobNodeAsync_throws_for_a_nonexistent_node()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out _);
		var sut = CreateSut(port);

		var act = () => sut.GetJobNodeAsync(new() { Context = ContextFor(owner), NodeId = new JobNodeId(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetJobChildrenAsync_applies_owner_and_archive_filters()
	{
		var owner = new AppUserId(10);
		var otherOwner = new AppUserId(11);
		var port = CreateSeededTree(owner, otherOwner, out _, out var branchId, out var leafId);
		var sut = CreateSut(port);

		var activeChildren = await sut.GetJobChildrenAsync(
			new() { Context = ContextFor(owner), ParentId = branchId });
		activeChildren.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(leafId);

		var ownedByOther = await sut.GetJobChildrenAsync(
			new() { Context = ContextFor(owner), ParentId = branchId, Ownership = OwnershipFilter.OwnedBy(otherOwner) });
		ownedByOther.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(leafId);

		var archivedOnly = await sut.GetJobChildrenAsync(
			new() { Context = ContextFor(owner), ParentId = branchId, ArchiveFilter = JobArchiveFilter.ArchivedOnly });
		archivedOnly.Should().ContainSingle(c => c.Description == "Old plumbing job");

		var all = await sut.GetJobChildrenAsync(
			new() { Context = ContextFor(owner), ParentId = branchId, ArchiveFilter = JobArchiveFilter.All });
		all.Should().HaveCount(2);
	}

	[Fact]
	public async Task GetJobSummariesAsync_describes_known_ids_and_silently_omits_unknown_ones()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out var branchId, out var leafId);
		var sut = CreateSut(port);

		var result = await sut.GetJobSummariesAsync(
			new() { Context = ContextFor(owner), NodeIds = [branchId, leafId, new(999)] });

		result.Select(r => r.Id).Should().BeEquivalentTo([branchId, leafId]);
	}

	[Fact]
	public async Task GetJobSummariesAsync_includes_reconciled_costs_when_the_actor_may_view_them()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out var branchId, out var leafId);
		var costQueries = new FakeCostQueries();
		costQueries.SeedBulkCost(branchId, new(90m));
		costQueries.SeedBulkCost(leafId, new(45m));
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSummariesAsync(
			new() { Context = ContextFor(owner), NodeIds = [branchId, leafId] });

		result.Single(r => r.Id == branchId).Cost.Should().Be(new Money(90m));
		result.Single(r => r.Id == leafId).Cost.Should().Be(new Money(45m));
		// Fresh-eyes review §2.8: one bulk call prices every row, never one round trip per row.
		costQueries.GetBulkNodeCostsCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetJobSummariesAsync_captures_the_costing_instant_once_for_the_whole_operation()
	{
		var operationInstant = Instant.FromUtc(2026, 7, 20, 12, 34, 56);
		var clock = new AdjustableClock(operationInstant);
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out var branchId, out _);
		var costQueries = new FakeCostQueries();
		costQueries.SeedBulkCost(branchId, new(90m));
		var sut = new JobQueries(
			EmployeePortMirroring(port), port, port, port, new FakeWorkSessionQueryPort(), new FakeLeafWorkQueryPort(),
			new FakePrerequisiteQueryPort(), new FakeScheduleQueryPort(), new FakeRateQueryPort(), costQueries, clock);

		_ = await sut.GetJobSummariesAsync(new() { Context = ContextFor(owner), NodeIds = [branchId] });

		clock.ReadCount.Should().Be(1);
		costQueries.LastBulkRequest.Should().NotBeNull();
		costQueries.LastBulkRequest!.AsOf.Should().Be(operationInstant);
	}

	[Fact]
	public async Task GetJobSummariesAsync_cost_enrichment_makes_one_bulk_call_no_matter_how_wide_the_page_is()
	{
		const int leafCount = 25;
		var owner = new AppUserId(10);
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(owner, EmployeeRole.Administrator);
		var leafIds = new List<JobNodeId>();
		for (var index = 0; index < leafCount; index++) {
			var leafId = new JobNodeId(100 + index);
			leafIds.Add(leafId);
			port.SeedNode(new() {
				Id = leafId,
				ParentId = null,
				Kind = NodeKind.Leaf,
				Description = $"Leaf {index}",
				PostedByUserId = owner,
				OwnerUserId = owner,
				Priority = Priority.Medium,
				PostedAt = port.NowToReturn,
				HasChildren = false,
				HasLeafWork = false,
				Version = 1,
			});
		}

		var costQueries = new FakeCostQueries();
		foreach (var leafId in leafIds) {
			costQueries.SeedBulkCost(leafId, new(10m));
		}

		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSummariesAsync(new() { Context = ContextFor(owner), NodeIds = [.. leafIds] });

		result.Should().HaveCount(leafCount);
		result.Should().OnlyContain(summary => summary.Cost == new Money(10m));
		costQueries.GetBulkNodeCostsCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetJobSubtreeAsync_returns_the_bounded_tree_with_computed_spans_and_omits_cost_without_access()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out var rootId, out var branchId, out var leafId);
		var costQueries = new FakeCostQueries();
		costQueries.DenyActor(owner);
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSubtreeAsync(
			new() { Context = ContextFor(owner), RootId = rootId, AsOf = port.NowToReturn });

		result.RootId.Should().Be(rootId);
		result.RootTotal.Should().BeNull();
		result.TzdbVersion.Should().BeNull();
		result.Nodes.Select(n => n.Id).Should().BeEquivalentTo([rootId, branchId, leafId]);
		result.Nodes.Should().OnlyContain(n => n.Cost == null);

		var root = result.Nodes.Single(n => n.Id == rootId);
		var branch = result.Nodes.Single(n => n.Id == branchId);
		var leaf = result.Nodes.Single(n => n.Id == leafId);
		root.Depth.Should().Be(0);
		branch.Depth.Should().Be(1);
		leaf.Depth.Should().Be(2);
		(root.SubtreeLft, root.SubtreeRgt).Should().Be((0, 5));
		(branch.SubtreeLft, branch.SubtreeRgt).Should().Be((1, 4));
		(leaf.SubtreeLft, leaf.SubtreeRgt).Should().Be((2, 3));
	}

	[Fact]
	public async Task GetJobSubtreeAsync_reports_each_rows_own_readiness()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out var rootId, out var branchId, out var leafId);
		var blockerId = new JobNodeId(90);
		port.SeedNode(new() {
			Id = blockerId,
			ParentId = rootId,
			Kind = NodeKind.Leaf,
			Description = "Unfinished blocker",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		// Declared on the branch, so ADR 0043 decision 1's downward inheritance gates the leaf too.
		await port.AddPrerequisiteAsync(new() { Context = ContextFor(owner), RequiredJobId = blockerId, DependentJobId = branchId });
		var costQueries = new FakeCostQueries();
		costQueries.DenyActor(owner);
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSubtreeAsync(
			new() { Context = ContextFor(owner), RootId = rootId, AsOf = port.NowToReturn });

		result.Nodes.Single(n => n.Id == branchId).IsReady.Should().BeFalse("the prerequisite is declared on it");
		result.Nodes.Single(n => n.Id == leafId).IsReady.Should().BeFalse("a prerequisite on an ancestor gates the whole subtree");
		result.Nodes.Single(n => n.Id == rootId).IsReady.Should().BeTrue("the root is above where the prerequisite is declared");
		result.Nodes.Single(n => n.Id == blockerId).IsReady.Should().BeTrue("nothing gates the blocker itself");
	}

	[Fact]
	public async Task GetJobSubtreeAsync_omits_cost_when_the_full_cost_hierarchy_exceeds_its_bound()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out var rootId, out var branchId, out var leafId);
		var costQueries = new FakeCostQueries();
		costQueries.FailHierarchyTotals(
			rootId,
			new ArgumentOutOfRangeException("request", 50_001, "This node's subtree exceeds the cost hierarchy maximum."));
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSubtreeAsync(
			new() { Context = ContextFor(owner), RootId = rootId, AsOf = port.NowToReturn });

		result.Nodes.Select(n => n.Id).Should().BeEquivalentTo([rootId, branchId, leafId]);
		result.RootTotal.Should().BeNull();
		result.TzdbVersion.Should().BeNull();
		result.Nodes.Should().OnlyContain(n => n.Cost == null);
		costQueries.GetHierarchyTotalsCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetJobSubtreeAsync_returns_nodes_in_pre_order_not_id_order()
	{
		var owner = new AppUserId(10);
		var rootId = new JobNodeId(1);
		var branchId = new JobNodeId(10);
		var leafId = new JobNodeId(3);
		var siblingId = new JobNodeId(20);
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(owner, EmployeeRole.Administrator);
		port.SeedNode(SubtreeNode(rootId, null, NodeKind.Root, "Root", owner, port.NowToReturn));
		port.SeedNode(SubtreeNode(branchId, rootId, NodeKind.Branch, "Branch", owner, port.NowToReturn));
		port.SeedNode(SubtreeNode(leafId, branchId, NodeKind.Leaf, "Leaf", owner, port.NowToReturn));
		port.SeedNode(SubtreeNode(siblingId, rootId, NodeKind.Leaf, "Sibling", owner, port.NowToReturn));
		var costQueries = new FakeCostQueries();
		costQueries.DenyActor(owner);
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSubtreeAsync(
			new() { Context = ContextFor(owner), RootId = rootId, AsOf = port.NowToReturn });

		result.Nodes.Select(n => n.Id).Should().ContainInOrder(rootId, branchId, leafId, siblingId);
		result.Nodes.Select(n => n.SubtreeLft).Should().BeInAscendingOrder();
	}

	[Fact]
	public async Task GetJobSubtreeAsync_includes_reconciled_cost_when_the_actor_may_view_it()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out var rootId, out var branchId, out var leafId);
		var costQueries = new FakeCostQueries();
		costQueries.SeedHierarchyTotals(rootId, new() {
			NodeId = rootId,
			ExactCosts = EquatableDictionaryFactory.CopyOf(
				new Dictionary<JobNodeId, Money> { [rootId] = new(90m), [branchId] = new(90m), [leafId] = new(90m) }),
			DisplayedCosts = EquatableDictionaryFactory.CopyOf(
				new Dictionary<JobNodeId, Money> { [rootId] = new(90m), [branchId] = new(90m), [leafId] = new(90m) }),
			TzdbVersion = "2025b",
		});
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSubtreeAsync(
			new() { Context = ContextFor(owner), RootId = rootId, AsOf = port.NowToReturn });

		result.RootTotal.Should().Be(new Money(90m));
		result.TzdbVersion.Should().Be("2025b");
		result.Nodes.Single(n => n.Id == leafId).Cost.Should().Be(new Money(90m));
		result.Nodes.Single(n => n.Id == branchId).Cost.Should().Be(new Money(90m));
		costQueries.GetHierarchyTotalsCallCount.Should().Be(
			1, "the roll-up is one batched call over the whole subtree, never per node (Stage 6 efficiency guard)");
	}

	/// <summary>
	///     ADR 0042: CanView's ownership carve-out admits the whole subtree at once, so per-node costs are
	///     filtered again. A worker owning the branch sees its roll-up (an aggregate, exposing no
	///     individual's rate) but not another worker's individual leaf cost, which alongside that leaf's
	///     visible session hours would reveal their hourly rate.
	/// </summary>
	[Fact]
	public async Task GetJobSubtreeAsync_hides_another_workers_leaf_cost_but_keeps_the_branch_roll_up()
	{
		var owner = new AppUserId(10);
		var otherOwner = new AppUserId(11);
		var port = CreateSeededTree(owner, otherOwner, out var rootId, out var branchId, out var leafId);
		// A plain Worker, not the Administrator the shared fixture seeds: Administrator/CostViewer are
		// exactly the roles the per-node filter defers to.
		port.SeedRoles(owner, EmployeeRole.Worker);
		var costQueries = new FakeCostQueries();
		costQueries.SeedHierarchyTotals(rootId, new() {
			NodeId = rootId,
			ExactCosts = EquatableDictionaryFactory.CopyOf(
				new Dictionary<JobNodeId, Money> { [rootId] = new(90m), [branchId] = new(90m), [leafId] = new(90m) }),
			DisplayedCosts = EquatableDictionaryFactory.CopyOf(
				new Dictionary<JobNodeId, Money> { [rootId] = new(90m), [branchId] = new(90m), [leafId] = new(90m) }),
			TzdbVersion = "2025b",
		});
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSubtreeAsync(
			new() { Context = ContextFor(owner), RootId = rootId, AsOf = port.NowToReturn });

		result.Nodes.Single(n => n.Id == leafId).Cost.Should().BeNull("the leaf is owned by another worker");
		result.RootTotal.Should().Be(new Money(90m), "a roll-up is an aggregate and exposes no individual rate");
	}

	/// <summary>Stage 6 efficiency guard: the cost roll-up stays one batched call as the subtree grows, never per node.</summary>
	[Fact]
	public async Task GetJobSubtreeAsync_batches_the_cost_roll_up_into_one_call_regardless_of_subtree_width()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out var rootId, out var branchId, out _);
		for (var i = 0; i < 30; i++) {
			port.SeedNode(new() {
				Id = new(100 + i),
				ParentId = branchId,
				Kind = NodeKind.Leaf,
				Description = $"Wide sibling {i}",
				PostedByUserId = owner,
				OwnerUserId = owner,
				Priority = Priority.Low,
				PostedAt = port.NowToReturn,
				HasChildren = false,
				HasLeafWork = false,
				Version = 1,
			});
		}

		var costQueries = new FakeCostQueries();
		costQueries.SeedHierarchyTotals(rootId, new() {
			NodeId = rootId,
			ExactCosts = EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money> { [rootId] = new(0m) }),
			DisplayedCosts = EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money> { [rootId] = new(0m) }),
			TzdbVersion = "2025b",
		});
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetJobSubtreeAsync(
			new() { Context = ContextFor(owner), RootId = rootId, AsOf = port.NowToReturn });

		result.Nodes.Should().HaveCountGreaterThan(30);
		costQueries.GetHierarchyTotalsCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetJobSubtreeAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeCostQueries());

		var act = () => sut.GetJobSubtreeAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task SearchJobNodesAsync_matches_case_insensitively()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out var leafId);
		var sut = CreateSut(port);

		var result = await sut.SearchJobNodesAsync(new() { Context = ContextFor(owner), SearchText = "CABINETS" });

		result.Select(r => r.Id).Should().ContainSingle().Which.Should().Be(leafId);
	}

	[Fact]
	public async Task SearchJobNodesAsync_throws_for_blank_search_text()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out _);
		var sut = CreateSut(port);

		var act = () => sut.SearchJobNodesAsync(new() { Context = ContextFor(owner), SearchText = "   " });

		await act.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_returns_only_ready_waiting_or_in_progress_leaves()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out var leafId);
		await port.AttachLeafWorkAsync(new() { Context = ContextFor(owner), JobNodeId = leafId });
		var sut = CreateSut(port);

		var result = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner) });

		result.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(leafId);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_applies_the_owner_filter()
	{
		var owner = new AppUserId(10);
		var otherOwner = new AppUserId(11);
		var port = CreateSeededTree(owner, otherOwner, out _, out _, out var leafId);
		await port.AttachLeafWorkAsync(new() { Context = ContextFor(owner), JobNodeId = leafId });
		var sut = CreateSut(port);

		var ownedByOther = await sut.GetAwaitingProgressAsync(
			new() { Context = ContextFor(owner), Ownership = OwnershipFilter.OwnedBy(otherOwner) });
		var ownedByOwner = await sut.GetAwaitingProgressAsync(
			new() { Context = ContextFor(owner), Ownership = OwnershipFilter.OwnedBy(owner) });

		ownedByOther.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(leafId);
		ownedByOwner.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_applies_the_subtree_filter()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, owner, out var rootId, out var branchId, out var leafId);
		await port.AttachLeafWorkAsync(new() { Context = ContextFor(owner), JobNodeId = leafId });
		var otherBranchId = new JobNodeId(10);
		var otherLeafId = new JobNodeId(11);
		port.SeedNode(new() {
			Id = otherBranchId,
			ParentId = rootId,
			Kind = NodeKind.Branch,
			Description = "Other branch",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = otherLeafId,
			ParentId = otherBranchId,
			Kind = NodeKind.Leaf,
			Description = "Other leaf",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		await port.AttachLeafWorkAsync(new() { Context = ContextFor(owner), JobNodeId = otherLeafId });
		var sut = CreateSut(port);

		var result = await sut.GetAwaitingProgressAsync(
			new() { Context = ContextFor(owner), SubtreeRootId = branchId });

		result.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(leafId);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_pages_without_gaps_or_overlap_preserving_order()
	{
		const int leafCount = 5;
		var owner = new AppUserId(10);
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(owner, EmployeeRole.Administrator);
		var rootId = new JobNodeId(199);
		port.SeedNode(new() {
			Id = rootId,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = true,
			HasLeafWork = false,
			Version = 1,
		});
		var leafIds = new List<JobNodeId>();
		for (var index = 0; index < leafCount; index++) {
			var leafId = new JobNodeId(200 + index);
			leafIds.Add(leafId);
			port.SeedNode(new() {
				Id = leafId,
				ParentId = rootId,
				Kind = NodeKind.Leaf,
				Description = $"Leaf {index}",
				PostedByUserId = owner,
				OwnerUserId = owner,
				Priority = Priority.Medium,
				PostedAt = port.NowToReturn,
				HasChildren = false,
				HasLeafWork = false,
				Version = 1,
			});
		}

		var sut = CreateSut(port);

		var firstPage = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner), Offset = 0, Limit = 2 });
		var secondPage = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner), Offset = 2, Limit = 2 });
		var thirdPage = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner), Offset = 4, Limit = 2 });
		var unpaged = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner) });

		firstPage.Should().HaveCount(2);
		secondPage.Should().HaveCount(2);
		thirdPage.Should().ContainSingle();
		var paged = firstPage.Concat(secondPage).Concat(thirdPage).Select(e => e.Id).ToArray();
		paged.Should().Equal(unpaged.Select(e => e.Id));
		paged.Distinct().Should().HaveCount(leafCount);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_applies_a_bounded_default_when_limit_is_omitted()
	{
		var owner = new AppUserId(10);
		var port = CreateAwaitingProgressPort(owner, AwaitingProgressPaging.DefaultPageSize + 1);
		var sut = CreateSut(port);

		var result = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner) });

		result.Should().HaveCount(AwaitingProgressPaging.DefaultPageSize);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_clamps_an_excessive_limit_to_the_maximum_page_size()
	{
		var owner = new AppUserId(10);
		var port = CreateAwaitingProgressPort(owner, AwaitingProgressPaging.MaxPageSize + 1);
		var sut = CreateSut(port);

		var result = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner), Limit = AwaitingProgressPaging.MaxPageSize + 1 });

		result.Should().HaveCount(AwaitingProgressPaging.MaxPageSize);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_rejects_a_negative_offset()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetAwaitingProgressAsync(new() { Context = ContextFor(new(10)), Offset = -1 });

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_rejects_a_non_positive_explicit_limit()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetAwaitingProgressAsync(new() { Context = ContextFor(new(10)), Limit = 0 });

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_includes_reconciled_costs_for_returned_leaves_when_the_actor_may_view_them()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out var leafId);
		await port.AttachLeafWorkAsync(new() { Context = ContextFor(owner), JobNodeId = leafId });
		var costQueries = new FakeCostQueries();
		costQueries.SeedBulkCost(leafId, new(90m));
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetAwaitingProgressAsync(new() { Context = ContextFor(owner) });

		result.Should().ContainSingle();
		result[0].Id.Should().Be(leafId);
		result[0].Cost.Should().Be(new Money(90m));
		costQueries.GetBulkNodeCostsCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_throws_when_the_subtree_root_does_not_exist()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out _);
		var sut = CreateSut(port);

		var act = () => sut.GetAwaitingProgressAsync(
			new() { Context = ContextFor(owner), SubtreeRootId = new JobNodeId(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetAwaitingProgressAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_worker_can_view_their_own_sessions_on_a_leaf()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkId = leaf, WorkedByUserId = worker });

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	// ADR 0041: recorded work is job data, which spec §7.3 makes viewable by every employee role,
	// so a Worker may now read another worker's sessions. Editing one remains gated separately by
	// WorkSessionAccessPolicy.CanManage's node-control rule, which this change does not touch.
	[Fact]
	public async Task A_worker_can_view_another_workers_sessions()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedLeaf(leaf);
		port.SeedSession(new() {
			Id = new(7),
			LeafWorkId = leaf,
			WorkedByUserId = otherWorker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkId = leaf, WorkedByUserId = otherWorker });

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(7));
	}

	[Fact]
	public async Task A_requester_cannot_view_sessions_at_all()
	{
		var requester = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(requester, EmployeeRole.Requester);
		port.SeedLeaf(leaf);
		var sut = CreateSut(port);

		var act = () => sut.GetLeafSessionsAsync(
			new() { Context = ContextFor(requester), LeafWorkId = leaf });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Omitting_the_worker_filter_returns_every_workers_sessions_on_the_leaf()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedLeaf(leaf);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		port.SeedSession(new() {
			Id = new(2),
			LeafWorkId = leaf,
			WorkedByUserId = otherWorker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 11, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 11, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkId = leaf });

		result.Should().HaveCount(2);
		// Most-recent-first ordering must hold across the union, not just within one worker's bucket.
		result.Select(s => s.Id).Should().ContainInOrder(new WorkSessionId(2), new WorkSessionId(1));
	}

	[Fact]
	public async Task A_job_manager_can_view_any_workers_sessions()
	{
		var manager = new AppUserId(20);
		var worker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(manager, EmployeeRole.JobManager);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() { Context = ContextFor(manager), LeafWorkId = leaf, WorkedByUserId = worker });

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	[Fact]
	public async Task Querying_sessions_for_a_nonexistent_leaf_throws_not_found()
	{
		var worker = new AppUserId(20);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		var sut = CreateSut(port);

		var act = () => sut.GetLeafSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkId = new(999), WorkedByUserId = worker });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetLeafSessionsAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeWorkSessionQueryPort());

		var act = () => sut.GetLeafSessionsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_worker_can_view_their_own_active_sessions_across_leaves()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkIds = [leaf] });

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	[Fact]
	public async Task GetActiveSessionsAsync_does_not_return_a_finished_session()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			FinishedAt = Instant.FromUtc(2026, 1, 1, 10, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 10, 0),
			Version = 2,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkIds = [leaf] });

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetActiveSessionsAsync_throws_when_the_actor_holds_no_manageable_role()
	{
		var worker = new AppUserId(20);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker);
		var sut = CreateSut(port);

		var act = () => sut.GetActiveSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkIds = [] });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task GetActiveSessionsAsync_filters_out_another_workers_session_for_a_plain_worker()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = otherWorker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() { Context = ContextFor(worker), LeafWorkIds = [leaf] });

		result.Should().BeEmpty("a plain Worker may not manage another worker's session, so the dashboard offers them Start, not Finish, for it");
	}

	/// <summary>
	///     Administrator/JobManager may finish any leaf's session unconditionally
	///     (<see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" />, ADR 0032), so this
	///     read surfaces another worker's active session to them too -- otherwise the dashboard would
	///     offer a Start button for work that is already in progress.
	/// </summary>
	[Fact]
	public async Task GetActiveSessionsAsync_includes_another_workers_session_for_an_administrator()
	{
		var administrator = new AppUserId(20);
		var worker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() { Context = ContextFor(administrator), LeafWorkIds = [leaf] });

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	[Fact]
	public async Task GetActiveSessionsAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeWorkSessionQueryPort());

		var act = () => sut.GetActiveSessionsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetLeafWorkAsync_returns_the_leafs_current_achievement()
	{
		var actor = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeLeafWorkQueryPort();
		port.Seed(new() {
			JobNodeId = leaf,
			Achievement = Achievement.InProgress,
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafWorkAsync(new() { Context = ContextFor(actor), JobNodeId = leaf });

		result.Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task GetLeafWorkAsync_throws_when_no_leaf_work_is_attached()
	{
		var actor = new AppUserId(20);
		var sut = CreateSut(new FakeLeafWorkQueryPort());

		var act = () => sut.GetLeafWorkAsync(new() { Context = ContextFor(actor), JobNodeId = new(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetLeafWorkAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeLeafWorkQueryPort());

		var act = () => sut.GetLeafWorkAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_returns_edges_in_either_direction()
	{
		var actor = new AppUserId(20);
		var required = new JobNodeId(30);
		var dependent = new JobNodeId(31);
		var port = new FakePrerequisiteQueryPort();
		port.SeedEdge(new(required, dependent));
		var sut = CreateSut(port);

		var requiredSide = await sut.GetPrerequisitesAsync(new() { Context = ContextFor(actor), NodeId = required });
		var dependentSide = await sut.GetPrerequisitesAsync(new() { Context = ContextFor(actor), NodeId = dependent });

		requiredSide.Should().ContainSingle(e => e.RequiredJobId == required && e.DependentJobId == dependent);
		dependentSide.Should().ContainSingle(e => e.RequiredJobId == required && e.DependentJobId == dependent);
	}

	[Fact]
	public async Task GetPrerequisitesAsync_throws_for_a_nonexistent_node()
	{
		var actor = new AppUserId(20);
		var sut = CreateSut(new FakePrerequisiteQueryPort());

		var act = () => sut.GetPrerequisitesAsync(new() { Context = ContextFor(actor), NodeId = new(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakePrerequisiteQueryPort());

		var act = () => sut.GetPrerequisitesAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_worker_can_view_their_own_schedule()
	{
		var worker = new AppUserId(20);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedVersion(new() {
			Id = new(1),
			UserId = worker,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"], new(2026, 1, 1), null,
				[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetScheduleAsync(new() { Context = ContextFor(worker), UserId = worker });

		result.Versions.Should().ContainSingle();
		result.Exceptions.Should().BeEmpty();
	}

	[Fact]
	public async Task A_worker_cannot_view_another_workers_schedule()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedEmployee(otherWorker);
		var sut = CreateSut(port);

		var act = () => sut.GetScheduleAsync(new() { Context = ContextFor(worker), UserId = otherWorker });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_can_view_another_employees_schedule()
	{
		var administrator = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var result = await sut.GetScheduleAsync(new() { Context = ContextFor(administrator), UserId = worker });

		result.Versions.Should().BeEmpty();
	}

	[Fact]
	public async Task Querying_a_nonexistent_employees_schedule_throws_not_found()
	{
		var administrator = new AppUserId(20);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		var sut = CreateSut(port);

		var act = () => sut.GetScheduleAsync(new() { Context = ContextFor(administrator), UserId = new(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetScheduleAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeScheduleQueryPort());

		var act = () => sut.GetScheduleAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task An_administrator_can_view_an_employees_rates()
	{
		var administrator = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeRateQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		port.SeedUserCostRate(new() {
			Id = new(1),
			UserId = worker,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetRatesAsync(new() { Context = ContextFor(administrator), UserId = worker });

		result.UserCostRates.Should().ContainSingle();
		result.NodeRateOverrides.Should().BeEmpty();
	}

	[Fact]
	public async Task A_cost_viewer_can_view_an_employees_rates()
	{
		var costViewer = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeRateQueryPort();
		port.SeedRoles(costViewer, EmployeeRole.CostViewer);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var result = await sut.GetRatesAsync(new() { Context = ContextFor(costViewer), UserId = worker });

		result.UserCostRates.Should().BeEmpty();
	}

	[Fact]
	public async Task A_rate_manager_without_cost_visibility_cannot_view_an_employees_rates()
	{
		var rateManager = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeRateQueryPort();
		port.SeedRoles(rateManager, EmployeeRole.RateManager);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var act = () => sut.GetRatesAsync(new() { Context = ContextFor(rateManager), UserId = worker });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_worker_cannot_view_their_own_rates()
	{
		var worker = new AppUserId(20);
		var port = new FakeRateQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var act = () => sut.GetRatesAsync(new() { Context = ContextFor(worker), UserId = worker });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Querying_a_nonexistent_employees_rates_throws_not_found()
	{
		var administrator = new AppUserId(20);
		var port = new FakeRateQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		var sut = CreateSut(port);

		var act = () => sut.GetRatesAsync(new() { Context = ContextFor(administrator), UserId = new(999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetRatesAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeRateQueryPort());

		var act = () => sut.GetRatesAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
