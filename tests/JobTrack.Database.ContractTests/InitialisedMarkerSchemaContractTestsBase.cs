namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-IDENT-002 (database column, marker half) contract for schema
///     slice 3: the singleton <c>initialised_marker</c> table, asserted
///     identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlInitialisedMarkerSchemaTests" /> and
///     <see cref="SqliteInitialisedMarkerSchemaTests" /> (impl plan §6.2 item 3,
///     ADR 0015 item 1). The permanent-root guard half of TC-DB-IDENT-002 lands
///     with schema slice 4.
/// </summary>
public abstract class InitialisedMarkerSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected InitialisedMarkerSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_initialised_marker_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection)).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_the_single_row_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		await InsertMarkerAsync(connection);

		(await CountRowsAsync(connection)).Should().Be(1);
	}

	[Fact]
	public async Task Inserting_a_second_row_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		await InsertMarkerAsync(connection);

		var act = async () => await ExecuteNonQueryAsync(
			connection, "INSERT INTO initialised_marker (id, initialised_at) VALUES (2, @initialisedAt);");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Updating_the_row_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		await InsertMarkerAsync(connection);

		var act = async () => await ExecuteNonQueryAsync(
			connection, "UPDATE initialised_marker SET initialised_at = @initialisedAt WHERE id = 1;");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_the_row_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		await InsertMarkerAsync(connection);

		var act = async () => await ExecuteNonQueryAsync(connection, "DELETE FROM initialised_marker WHERE id = 1;");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Concurrent_inserts_apply_exactly_once()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(TryInsertMarkerAsync(connectionA), TryInsertMarkerAsync(connectionB));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInitialisedAt(DateTimeOffset value);

	private async Task<DbConnection> OpenDeployedConnectionAsync()
	{
		var connection = await OpenExistingConnectionAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private async Task InsertMarkerAsync(DbConnection connection) =>
		await ExecuteNonQueryAsync(connection, "INSERT INTO initialised_marker (id, initialised_at) VALUES (1, @initialisedAt);");

	private async Task<bool> TryInsertMarkerAsync(DbConnection connection)
	{
		try {
			await InsertMarkerAsync(connection);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task ExecuteNonQueryAsync(DbConnection connection, string commandText)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = commandText;

		if (commandText.Contains("@initialisedAt", StringComparison.Ordinal)) {
			var parameter = command.CreateParameter();
			parameter.ParameterName = "@initialisedAt";
			parameter.Value = EncodeInitialisedAt(DateTimeOffset.UtcNow);
			command.Parameters.Add(parameter);
		}

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> CountRowsAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM initialised_marker;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}
}
