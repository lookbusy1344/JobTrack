namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Rates;
using NodaTime;

public sealed class RateCommandsTests
{
	private static readonly AppUserId AdministratorId = new(1);
	private static readonly AppUserId RateManagerId = new(2);
	private static readonly AppUserId WorkerId = new(3);
	private static readonly JobNodeId NodeId = new(10);

	private static FakeRateCommandPort CreateSeededPort()
	{
		var port = new FakeRateCommandPort();
		port.SeedRoles(AdministratorId, EmployeeRole.Administrator);
		port.SeedRoles(RateManagerId, EmployeeRole.RateManager);
		port.SeedRoles(WorkerId, EmployeeRole.Worker);
		port.SeedNode(NodeId);

		return port;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	[Fact]
	public async Task A_rate_manager_can_add_a_user_cost_rate()
	{
		var sut = new RateCommands(CreateSeededPort());
		var rate = new UserCostRate(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null);

		var result = await sut.AddUserCostRateAsync(new() { Context = ContextFor(RateManagerId), UserId = WorkerId, Rate = rate });

		result.UserId.Should().Be(WorkerId);
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task A_worker_cannot_add_a_user_cost_rate()
	{
		var sut = new RateCommands(CreateSeededPort());
		var rate = new UserCostRate(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null);

		var act = () => sut.AddUserCostRateAsync(new() { Context = ContextFor(WorkerId), UserId = WorkerId, Rate = rate });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_for_a_nonexistent_employee_throws_not_found()
	{
		var sut = new RateCommands(CreateSeededPort());
		var rate = new UserCostRate(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null);

		var act = () => sut.AddUserCostRateAsync(new() { Context = ContextFor(RateManagerId), UserId = new(999), Rate = rate });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Overlapping_user_cost_rates_for_the_same_employee_throw_an_invariant_violation()
	{
		var sut = new RateCommands(CreateSeededPort());
		await sut.AddUserCostRateAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Rate = new(
				new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 6, 1, 0, 0)),
		});

		var act = () => sut.AddUserCostRateAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Rate = new(new(30m), Instant.FromUtc(2026, 3, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("user-cost-rate-overlap");
	}

	[Fact]
	public async Task An_administrator_can_add_a_node_rate_override()
	{
		var sut = new RateCommands(CreateSeededPort());
		var over = new NodeRateOverride(NodeId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null);

		var result = await sut.AddNodeRateOverrideAsync(new() { Context = ContextFor(AdministratorId), UserId = WorkerId, Override = over });

		result.UserId.Should().Be(WorkerId);
		result.Override.NodeId.Should().Be(NodeId);
	}

	[Fact]
	public async Task A_worker_cannot_add_a_node_rate_override()
	{
		var sut = new RateCommands(CreateSeededPort());
		var over = new NodeRateOverride(NodeId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null);

		var act = () => sut.AddNodeRateOverrideAsync(new() { Context = ContextFor(WorkerId), UserId = WorkerId, Override = over });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_node_rate_override_for_a_nonexistent_node_throws_not_found()
	{
		var sut = new RateCommands(CreateSeededPort());
		var over = new NodeRateOverride(new(999), new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null);

		var act = () => sut.AddNodeRateOverrideAsync(new() { Context = ContextFor(RateManagerId), UserId = WorkerId, Override = over });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Overlapping_node_rate_overrides_for_the_same_node_and_employee_throw_an_invariant_violation()
	{
		var sut = new RateCommands(CreateSeededPort());
		await sut.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Override = new(
				NodeId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 6, 1, 0, 0)),
		});

		var act = () => sut.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Override = new(NodeId, new(45m), Instant.FromUtc(2026, 3, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("node-rate-override-overlap");
	}

	[Fact]
	public async Task A_node_rate_override_for_a_different_employee_on_the_same_node_does_not_overlap()
	{
		var port = CreateSeededPort();
		var otherWorker = new AppUserId(4);
		port.SeedUser(otherWorker);
		var sut = new RateCommands(port);
		await sut.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Override = new(NodeId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var result = await sut.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = otherWorker,
			Override = new(NodeId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.UserId.Should().Be(otherWorker);
	}

	[Fact]
	public async Task A_rate_manager_can_correct_a_user_cost_rate()
	{
		var sut = new RateCommands(CreateSeededPort());
		var added = await sut.AddUserCostRateAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var result = await sut.CorrectUserCostRateAsync(new() {
			Context = ContextFor(RateManagerId),
			RateId = added.Id,
			UserId = WorkerId,
			Version = added.Version,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.Rate.Rate.AmountPerHour.Should().Be(30m);
	}

	[Fact]
	public async Task A_worker_cannot_correct_a_user_cost_rate()
	{
		var sut = new RateCommands(CreateSeededPort());
		var added = await sut.AddUserCostRateAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => sut.CorrectUserCostRateAsync(new() {
			Context = ContextFor(WorkerId),
			RateId = added.Id,
			UserId = WorkerId,
			Version = added.Version,
			Reason = "Attempted correction",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var sut = new RateCommands(CreateSeededPort());
		var added = await sut.AddUserCostRateAsync(new() {
			Context = ContextFor(RateManagerId),
			UserId = WorkerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => sut.CorrectUserCostRateAsync(new() {
			Context = ContextFor(RateManagerId),
			RateId = added.Id,
			UserId = WorkerId,
			Version = added.Version + 1,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task An_administrator_can_correct_a_node_rate_override()
	{
		var sut = new RateCommands(CreateSeededPort());
		var added = await sut.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(AdministratorId),
			UserId = WorkerId,
			Override = new(NodeId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var result = await sut.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(AdministratorId),
			OverrideId = added.Id,
			UserId = WorkerId,
			Version = added.Version,
			Reason = "Corrected the override rate",
			Override = new(NodeId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.Override.Rate.AmountPerHour.Should().Be(45m);
	}

	[Fact]
	public void Constructor_rejects_a_null_port()
	{
		var act = () => new RateCommands(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task AddUserCostRateAsync_rejects_a_null_request()
	{
		var sut = new RateCommands(CreateSeededPort());

		var act = () => sut.AddUserCostRateAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task CorrectUserCostRateAsync_rejects_a_null_request()
	{
		var sut = new RateCommands(CreateSeededPort());

		var act = () => sut.CorrectUserCostRateAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task CorrectNodeRateOverrideAsync_rejects_a_null_request()
	{
		var sut = new RateCommands(CreateSeededPort());

		var act = () => sut.CorrectNodeRateOverrideAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task AddNodeRateOverrideAsync_rejects_a_null_request()
	{
		var sut = new RateCommands(CreateSeededPort());

		var act = () => sut.AddNodeRateOverrideAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
