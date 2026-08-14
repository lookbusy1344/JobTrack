namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="IRateQueryPort" /> (plan §8.5 slice 7), asserted identically
///     against PostgreSQL and SQLite by one thin sealed subclass per provider's own test project --
///     same shape as <see cref="ScheduleQueryPortContractTestsBase" />. Seeds a user cost rate and a
///     node rate override via the real <see cref="IInstallationBootstrapPort" />/<see cref="IRateCommandPort" />.
/// </summary>
public abstract class RateQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected RateQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetRatesAsync_returns_the_employees_cost_rates_and_node_overrides()
	{
		var (_, workerId, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetRatesAsync(workerId, workerId);

		result.UserCostRates.Should().ContainSingle();
		result.NodeRateOverrides.Should().ContainSingle();
	}

	[Fact]
	public async Task GetRatesAsync_returns_the_actors_current_roles()
	{
		var (administratorId, workerId, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetRatesAsync(administratorId, workerId);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetRatesAsync_returns_empty_for_an_employee_with_no_rate_data()
	{
		var (administratorId, _, _) = await SeedRatesAsync();
		var otherWorkerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Alan Turing", "alan.turing.rate-query", EmployeeRole.Worker);
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetRatesAsync(administratorId, otherWorkerId);

		result.UserCostRates.Should().BeEmpty();
		result.NodeRateOverrides.Should().BeEmpty();
	}

	[Fact]
	public async Task GetRatesAsync_throws_for_a_nonexistent_actor()
	{
		var (administratorId, workerId, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetRatesAsync(new(administratorId.Value + 999), workerId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetRatesAsync_throws_for_a_nonexistent_employee()
	{
		var (administratorId, _, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetRatesAsync(administratorId, new(administratorId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IRateCommandPort CreateCommandPort(string connectionString);

	internal abstract IRateQueryPort CreateQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	private async Task<(AppUserId AdministratorId, AppUserId WorkerId, JobNodeId RootJobNodeId)> SeedRatesAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace.rate-query",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;

		var workerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Grace Hopper", "grace.hopper.rate-query", EmployeeRole.Worker);

		// Overrides target a child node, never the root (ADR 0069).
		var child = await CreateJobNodePort(database.ConnectionString).AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Overridable leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});

		var commandPort = CreateCommandPort(database.ConnectionString);
		_ = await commandPort.AddUserCostRateAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		_ = await commandPort.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(child.Id, new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		return (administratorId, workerId, child.Id);
	}
}
