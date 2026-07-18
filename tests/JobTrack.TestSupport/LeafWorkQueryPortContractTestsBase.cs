namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;

/// <summary>
///     Shared contract for <see cref="ILeafWorkQueryPort" /> (plan §8.5 slice 5), asserted identically
///     against PostgreSQL and SQLite by one thin sealed subclass per provider's own test project --
///     same shape as <see cref="JobBrowseQueryPortContractTestsBase" />. Seeds a leaf with attached
///     <c>LeafWork</c> via the real <see cref="IInstallationBootstrapPort" />/<see cref="IJobNodeCommandPort" />.
/// </summary>
public abstract class LeafWorkQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected LeafWorkQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetLeafWorkAsync_returns_the_attached_leaf_work()
	{
		var leafId = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetLeafWorkAsync(leafId);

		result.JobNodeId.Should().Be(leafId);
		result.Achievement.Should().Be(Achievement.Waiting);
	}

	[Fact]
	public async Task GetLeafWorkAsync_throws_when_no_leaf_work_is_attached()
	{
		var (_, bareLeafId) = await SeedBareLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetLeafWorkAsync(bareLeafId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateJobCommandPort(string connectionString);

	protected abstract ILeafWorkQueryPort CreateQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private async Task<JobNodeId> SeedWorkedLeafAsync()
	{
		var (administratorId, leafId) = await SeedBareLeafAsync();
		var jobCommandPort = CreateJobCommandPort(database.ConnectionString);
		_ = await jobCommandPort.AttachLeafWorkAsync(new() { Context = ContextFor(administratorId), JobNodeId = leafId });

		return leafId;
	}

	private async Task<(AppUserId AdministratorId, JobNodeId LeafId)> SeedBareLeafAsync()
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
		var leaf = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Pour foundation",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});

		return (administratorId, leaf.Id);
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}
}
