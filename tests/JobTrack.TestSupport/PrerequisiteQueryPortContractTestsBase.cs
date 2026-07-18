namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;

/// <summary>
///     Shared contract for <see cref="IPrerequisiteQueryPort" /> (plan §8.5 slice 5), asserted
///     identically against PostgreSQL and SQLite by one thin sealed subclass per provider's own test
///     project -- same shape as <see cref="JobBrowseQueryPortContractTestsBase" />. Seeds two leaves and
///     a prerequisite edge between them via the real
///     <see cref="IInstallationBootstrapPort" />/<see cref="IJobNodeCommandPort" />.
/// </summary>
public abstract class PrerequisiteQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected PrerequisiteQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetPrerequisitesAsync_returns_the_edge_from_the_required_side()
	{
		var (requiredId, dependentId, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetPrerequisitesAsync(requiredId);

		result.Should().ContainSingle(e => e.RequiredJobId == requiredId && e.DependentJobId == dependentId);
	}

	[Fact]
	public async Task GetPrerequisitesAsync_returns_the_edge_from_the_dependent_side()
	{
		var (requiredId, dependentId, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetPrerequisitesAsync(dependentId);

		result.Should().ContainSingle(e => e.RequiredJobId == requiredId && e.DependentJobId == dependentId);
	}

	[Fact]
	public async Task GetPrerequisitesAsync_returns_empty_for_a_node_with_no_edges()
	{
		var (_, _, unrelatedId) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetPrerequisitesAsync(unrelatedId);

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_bounds_results_by_offset_and_limit()
	{
		var (requiredId, _, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var firstPage = await port.GetPrerequisitesAsync(requiredId, 0, 1);
		var secondPage = await port.GetPrerequisitesAsync(requiredId, 1, 1);

		firstPage.Should().ContainSingle();
		secondPage.Should().BeEmpty();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_throws_for_a_nonexistent_node()
	{
		var (requiredId, _, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetPrerequisitesAsync(new(requiredId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateJobCommandPort(string connectionString);

	protected abstract IPrerequisiteQueryPort CreateQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	/// <summary>Seeds two leaves with a prerequisite edge (required -&gt; dependent) and a third, unrelated leaf.</summary>
	private async Task<(JobNodeId RequiredId, JobNodeId DependentId, JobNodeId UnrelatedId)> SeedEdgeAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;

		var jobCommandPort = CreateJobCommandPort(database.ConnectionString);
		var required = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Pour foundation",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var dependent = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Frame walls",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var unrelated = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Paint fence",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		await jobCommandPort.AddPrerequisiteAsync(new() {
			Context = ContextFor(administratorId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		return (required.Id, dependent.Id, unrelated.Id);
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}
}
