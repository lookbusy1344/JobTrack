namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="IRateCommandPort" /> (impl plan §7.4 step 3, §7.3 slice 9: add
///     user rates and node overrides), asserted identically against PostgreSQL and SQLite by one thin
///     sealed subclass per provider's own test project -- same shape as
///     <see cref="ScheduleCommandPortContractTestsBase" />. Mirrors <c>RateCommandsTests</c>' scenarios
///     against the fake port, so the real persistence implementations are held to the same behavioural
///     contract. Node rate overrides target the permanent root job node the real bootstrap port
///     already creates, so no separate job-node seeding step is needed.
/// </summary>
public abstract class RateCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected RateCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_rate_manager_can_add_a_user_cost_rate()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var result = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.UserId.Should().Be(workerId);
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_writes_an_audit_event()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var result = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "user_cost_rate",
				EntityId = result.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle();
		audit.Events[0].Operation.Should().Be("add-user-cost-rate");
		audit.Events[0].ActorId.Should().Be(rateManagerId);
		audit.Events[0].AfterData!.Value["amount_per_hour"].Should().Be("25");
	}

	[Fact]
	public async Task A_worker_cannot_add_a_user_cost_rate()
	{
		var (_, _, _, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddUserCostRateAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_for_a_nonexistent_employee_throws_not_found()
	{
		var (_, _, _, rateManagerId, _) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = new(rateManagerId.Value + 999),
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Overlapping_user_cost_rates_for_the_same_employee_throw_an_invariant_violation()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(
				new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 6, 1, 0, 0)),
		});

		var act = () => port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(30m), Instant.FromUtc(2026, 3, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("user-cost-rate-overlap");
	}

	[Fact]
	public async Task An_administrator_can_add_a_node_rate_override()
	{
		var (_, childId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var result = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.UserId.Should().Be(workerId);
		result.Override.NodeId.Should().Be(childId);
	}

	[Fact]
	public async Task A_worker_cannot_add_a_node_rate_override()
	{
		var (_, childId, _, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Override = new(childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_node_rate_override_for_a_nonexistent_node_throws_not_found()
	{
		var (rootId, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Override = new(
				new(rootId.Value + 999), new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Adding_a_node_rate_override_on_the_root_throws_an_invariant_violation()
	{
		var (rootId, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Override = new(rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("node-rate-override-on-root");
	}

	[Fact]
	public async Task Correcting_a_node_rate_override_onto_the_root_throws_an_invariant_violation()
	{
		var (rootId, childId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Re-pointing at the root",
			Override = new(rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("node-rate-override-on-root");
	}

	[Fact]
	public async Task Overlapping_node_rate_overrides_for_the_same_node_and_employee_throw_an_invariant_violation()
	{
		var (_, childId, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Override = new(
				childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 6, 1, 0, 0)),
		});

		var act = () => port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Override = new(childId, new(45m), Instant.FromUtc(2026, 3, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("node-rate-override-overlap");
	}

	[Fact]
	public async Task A_rate_manager_can_correct_a_user_cost_rate()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var result = await port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.Rate.Rate.AmountPerHour.Should().Be(30m);
		result.Version.Should().Be(added.Version + 1);
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_writes_an_audit_event()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		_ = await port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "user_cost_rate",
				EntityId = added.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().Contain(e => e.Operation == "correct-user-cost-rate" && e.ActorId == rateManagerId);
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version + 1,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_into_overlap_with_another_throws_an_invariant_violation()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(
				new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 3, 1, 0, 0)),
		});
		var toCorrect = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(30m), Instant.FromUtc(2026, 6, 1, 0, 0), null),
		});

		var act = () => port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = toCorrect.Id,
			UserId = workerId,
			Version = toCorrect.Version,
			Reason = "Moved the start date earlier",
			Rate = new(new(30m), Instant.FromUtc(2026, 2, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("user-cost-rate-overlap");
	}

	[Fact]
	public async Task A_worker_cannot_correct_a_user_cost_rate()
	{
		var (_, _, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(workerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Attempted correction",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_can_correct_a_node_rate_override()
	{
		var (_, childId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var result = await port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the override rate",
			Override = new(childId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.Override.Rate.AmountPerHour.Should().Be(45m);
		result.Version.Should().Be(added.Version + 1);
	}

	[Fact]
	public async Task Correcting_a_node_rate_override_writes_an_audit_event()
	{
		var (_, childId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		_ = await port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the override rate",
			Override = new(childId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "node_rate_override",
				EntityId = added.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().Contain(e => e.Operation == "correct-node-rate-override" && e.ActorId == administratorId);
	}

	[Fact]
	public async Task Correcting_a_node_rate_override_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, childId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = added.Id,
			UserId = workerId,
			Version = added.Version + 1,
			Reason = "Corrected the override rate",
			Override = new(childId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Correcting_a_node_rate_override_into_overlap_with_another_throws_an_invariant_violation()
	{
		var (_, childId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(
				childId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 3, 1, 0, 0)),
		});
		var toCorrect = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(childId, new(45m), Instant.FromUtc(2026, 6, 1, 0, 0), null),
		});

		var act = () => port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = toCorrect.Id,
			UserId = workerId,
			Version = toCorrect.Version,
			Reason = "Moved the start date earlier",
			Override = new(childId, new(45m), Instant.FromUtc(2026, 2, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("node-rate-override-overlap");
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IRateCommandPort CreateRatePort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	/// <summary>
	///     Seeds a deployed schema, an administrator via the real bootstrap port (which also
	///     creates the permanent root job node and grants <see cref="EmployeeRole.Administrator" />),
	///     a <see cref="EmployeeRole.RateManager" /> employee, one <see cref="EmployeeRole.Worker" />
	///     employee, and one child job node under the root. Node overrides target the child, never the
	///     root (ADR 0069): the root is the only node the bootstrap creates, so a legal override target
	///     must be seeded explicitly.
	/// </summary>
	private async Task<(JobNodeId RootId, JobNodeId ChildId, AppUserId AdministratorId, AppUserId RateManagerId, AppUserId WorkerId)>
		SeedAdministratorRateManagerAndWorkerAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		var rateManagerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Katherine Jones", "katherine.jones.rate", EmployeeRole.RateManager);
		var workerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Grace Hopper", "grace.hopper.rate", EmployeeRole.Worker);

		var child = await CreateJobNodePort(database.ConnectionString).AddChildAsync(new() {
			Context = ContextFor(result.AdministratorId),
			ParentId = result.RootJobNodeId,
			Description = "Overridable leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});

		return (result.RootJobNodeId, child.Id, result.AdministratorId, rateManagerId, workerId);
	}
}
