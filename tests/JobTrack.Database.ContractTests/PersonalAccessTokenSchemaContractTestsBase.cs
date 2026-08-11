namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-TOKEN-001 contract for the <c>personal_access_token</c> table (ADR 0029), asserted
///     identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlPersonalAccessTokenSchemaTests" /> and
///     <see cref="SqlitePersonalAccessTokenSchemaTests" />.
/// </summary>
public abstract class PersonalAccessTokenSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected PersonalAccessTokenSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_personal_access_token_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection)).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_a_personal_access_token_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertTokenAsync(connection, userId, "test-token-hash-1", "my-cli", 30);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Inserting_a_token_with_a_blank_label_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertTokenAsync(connection, userId, "test-token-hash-2", "   ", 30);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_token_whose_expiry_is_not_after_its_creation_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertTokenAsync(connection, userId, "test-token-hash-3", "my-cli", -1);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_two_tokens_with_the_same_hash_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedAppUserAsync(connection, "Alice Example");
		_ = await InsertTokenAsync(connection, userId, "duplicate-hash", "first", 30);

		var act = async () => await InsertTokenAsync(connection, userId, "duplicate-hash", "second", 30);

		await act.Should().ThrowAsync<DbException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract Task<long> InsertTokenAsync(
		DbConnection connection, long appUserId, string tokenHash, string label, int expiresInDays);

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

	private static async Task<long> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		return Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<long> CountRowsAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM personal_access_token;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	protected static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
