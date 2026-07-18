namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-SCHEMA-001..005 contract (docs/traceability/test-catalogue.md
///     §3), asserted identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlSchemaDeploymentTests" /> and
///     <see cref="SqliteSchemaDeploymentTests" /> (impl plan §6.6).
/// </summary>
public abstract class SchemaDeploymentContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected SchemaDeploymentContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	// TC-DB-SCHEMA-001
	[Fact]
	public async Task Deploying_from_empty_applies_script_and_records_it()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));

		await using var connection = await OpenNewConnectionAsync();
		await CreateDeployer(connection).DeployAsync(scripts, CancellationToken.None);

		var appliedVersions = await ReadAppliedVersionsAsync(connection);
		appliedVersions.Should().HaveCount(scripts.Count);

		var applied = appliedVersions[0];
		applied.Version.Should().Be(scripts[0].Version);
		applied.Checksum.Should().Be(scripts[0].Checksum);
		applied.ApplicationVersion.Should().Be(ApplicationVersion);
		applied.AppliedBy.Should().Be(AppliedBy);
		applied.AppliedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

		(await ReadNamesAsync(connection, "achievement_status")).Should()
			.BeEquivalentTo("Waiting", "InProgress", "Success", "Cancelled", "Unsuccessful");
	}

	// TC-DB-SCHEMA-002
	[Fact]
	public async Task Redeploying_an_up_to_date_database_is_a_no_op()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));

		await using var connection = await OpenNewConnectionAsync();
		var deployer = CreateDeployer(connection);
		await deployer.DeployAsync(scripts, CancellationToken.None);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		(await ReadAppliedVersionsAsync(connection)).Should().HaveCount(scripts.Count);
	}

	// TC-DB-SCHEMA-003
	[Fact]
	public async Task Script_edited_after_being_applied_is_rejected()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));

		await using var connection = await OpenNewConnectionAsync();
		var deployer = CreateDeployer(connection);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		var tamperedScript = scripts[0] with { Checksum = "tampered-checksum" };

		var act = async () => await deployer.DeployAsync([tamperedScript], CancellationToken.None);

		await act.Should().ThrowAsync<SchemaDeploymentException>();
	}

	// TC-DB-SCHEMA-004
	[Fact]
	public async Task Applied_version_newer_than_any_known_script_is_rejected()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));

		await using var connection = await OpenNewConnectionAsync();
		var deployer = CreateDeployer(connection);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		await using (var transaction = await connection.BeginTransactionAsync()) {
			await CreateStore().RecordAppliedVersionAsync(
				connection,
				transaction,
				new() {
					Version = scripts[^1].Version + 1,
					Description = "from-the-future",
					Checksum = "unknown",
					ApplicationVersion = ApplicationVersion,
					AppliedBy = AppliedBy,
					AppliedAtUtc = DateTimeOffset.UtcNow,
				},
				CancellationToken.None);
			await transaction.CommitAsync();
		}

		var act = async () => await deployer.DeployAsync(scripts, CancellationToken.None);

		await act.Should().ThrowAsync<SchemaDeploymentException>();
	}

	// TC-DB-SCHEMA-005-PG / TC-DB-SCHEMA-005-SQ
	[Fact]
	public async Task Concurrent_deployment_runs_apply_exactly_once()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));

		await using var connectionA = await OpenNewConnectionAsync();
		await using var connectionB = await OpenNewConnectionAsync();

		var deployerA = new SchemaDeployer(connectionA, CreateStore(), CreateLockStrategy(), ApplicationVersion, "runner-a");
		var deployerB = new SchemaDeployer(connectionB, CreateStore(), CreateLockStrategy(), ApplicationVersion, "runner-b");

		await Task.WhenAll(
			deployerA.DeployAsync(scripts, CancellationToken.None),
			deployerB.DeployAsync(scripts, CancellationToken.None));

		await using var verifyConnection = await OpenNewConnectionAsync();
		(await ReadAppliedVersionsAsync(verifyConnection)).Should().HaveCount(scripts.Count);
		(await ReadNamesAsync(verifyConnection, "achievement_status")).Should().HaveCount(5);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	private SchemaDeployer CreateDeployer(DbConnection connection) =>
		new(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);

	private async Task<DbConnection> OpenNewConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		return connection;
	}

	private async Task<IReadOnlyList<AppliedSchemaVersion>> ReadAppliedVersionsAsync(DbConnection connection)
	{
		await using var transaction = await connection.BeginTransactionAsync();
		var appliedVersions = await CreateStore().GetAppliedVersionsAsync(connection, transaction, CancellationToken.None);
		await transaction.CommitAsync();
		return appliedVersions;
	}

	private static async Task<IReadOnlyList<string>> ReadNamesAsync(DbConnection connection, string tableName)
	{
		var names = new List<string>();

		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT name FROM {tableName} ORDER BY id;";

		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			names.Add(reader.GetString(0));
		}

		return names;
	}
}
