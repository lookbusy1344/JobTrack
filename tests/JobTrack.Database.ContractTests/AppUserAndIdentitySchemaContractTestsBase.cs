namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-IDENT-001 (database column) contract for schema slice 2:
///     <c>app_user</c>, Identity credential/role storage, and their 1:1 link,
///     asserted identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlAppUserAndIdentitySchemaTests" /> and
///     <see cref="SqliteAppUserAndIdentitySchemaTests" /> (impl plan §6.2 item 2).
/// </summary>
public abstract class AppUserAndIdentitySchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string PasswordHash = "placeholder-hash";
	private const string SecurityStamp = "placeholder-security-stamp";
	private const string ConcurrencyStamp = "placeholder-concurrency-stamp";
	private const short AdministratorRoleId = 1;
	private const short UnknownRoleId = -1;

	private static readonly IReadOnlyList<string> SeededRoleNames =
		["Administrator", "Job manager", "Worker", "Rate manager", "Cost viewer", "Auditor", "Requester"];

	private readonly IDisposableTestDatabase database;

	protected AppUserAndIdentitySchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_app_user_identity_user_and_seeded_roles()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await ReadRoleNamesAsync(connection)).Should().BeEquivalentTo(SeededRoleNames);
	}

	[Fact]
	public async Task Linking_an_identity_user_to_an_app_user_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var appUserId = await InsertAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, appUserId, "alice", "ALICE");

		identityUserId.Should().BePositive();
	}

	[Fact]
	public async Task Duplicate_normalized_user_name_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var firstAppUserId = await InsertAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, firstAppUserId, "alice", "ALICE");

		var secondAppUserId = await InsertAppUserAsync(connection, "Alice Duplicate");

		var act = async () => await InsertIdentityUserAsync(connection, secondAppUserId, "alice2", "ALICE");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_normalized_user_name_differing_only_by_case_from_an_existing_one_is_rejected()
	{
		// Deliberately bypasses ILookupNormalizer's case-folding -- InsertIdentityUserAsync writes
		// normalized_user_name exactly as given, so this proves the defense-in-depth expression
		// index (remediation plan §3.4) catches a case-varying value the application normalizer
		// should never have produced, not just an exact duplicate.
		await using var connection = await OpenDeployedConnectionAsync();

		var firstAppUserId = await InsertAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, firstAppUserId, "alice", "ALICE");

		var secondAppUserId = await InsertAppUserAsync(connection, "Alice Duplicate");

		var act = async () => await InsertIdentityUserAsync(connection, secondAppUserId, "alice2", "alice");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_second_identity_user_cannot_link_to_an_already_linked_app_user()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var appUserId = await InsertAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, appUserId, "alice", "ALICE");

		var act = async () => await InsertIdentityUserAsync(connection, appUserId, "alice2", "ALICE2");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Blank_display_name_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await InsertAppUserAsync(connection, "   ");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_an_app_user_referenced_by_an_identity_user_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var appUserId = await InsertAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, appUserId, "alice", "ALICE");

		var act = async () => await ExecuteNonQueryAsync(connection, "DELETE FROM app_user WHERE id = @id;", ("@id", appUserId));

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Assigning_the_same_role_twice_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var appUserId = await InsertAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, appUserId, "alice", "ALICE");
		await AssignRoleAsync(connection, identityUserId, AdministratorRoleId);

		var act = async () => await AssignRoleAsync(connection, identityUserId, AdministratorRoleId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Assigning_an_unknown_role_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var appUserId = await InsertAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, appUserId, "alice", "ALICE");

		var act = async () => await AssignRoleAsync(connection, identityUserId, UnknownRoleId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Concurrent_inserts_racing_on_the_same_normalized_user_name_apply_exactly_once()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var firstAppUserId = await InsertAppUserAsync(seedConnection, "Alice Example");
		var secondAppUserId = await InsertAppUserAsync(seedConnection, "Alice Duplicate");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertIdentityUserAsync(connectionA, firstAppUserId, "alice", "ALICE"),
			TryInsertIdentityUserAsync(connectionB, secondAppUserId, "alice-b", "ALICE"));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

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

	private static async Task<long> InsertAppUserAsync(DbConnection connection, string displayName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone)
							  VALUES (@displayName, 'Europe/London')
							  RETURNING id;
							  """;
		AddParameter(command, "@displayName", displayName);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<long> InsertIdentityUserAsync(
		DbConnection connection, long appUserId, string userName, string normalizedUserName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO identity_user
							  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp)
							  VALUES (@appUserId, @userName, @normalizedUserName, @passwordHash, @securityStamp, @concurrencyStamp)
							  RETURNING id;
							  """;
		AddParameter(command, "@appUserId", appUserId);
		AddParameter(command, "@userName", userName);
		AddParameter(command, "@normalizedUserName", normalizedUserName);
		AddParameter(command, "@passwordHash", PasswordHash);
		AddParameter(command, "@securityStamp", SecurityStamp);
		AddParameter(command, "@concurrencyStamp", ConcurrencyStamp);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<bool> TryInsertIdentityUserAsync(
		DbConnection connection, long appUserId, string userName, string normalizedUserName)
	{
		try {
			await InsertIdentityUserAsync(connection, appUserId, userName, normalizedUserName);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private static async Task AssignRoleAsync(DbConnection connection, long identityUserId, short roleId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
							  VALUES (@identityUserId, @roleId);
							  """;
		AddParameter(command, "@identityUserId", identityUserId);
		AddParameter(command, "@roleId", roleId);

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task ExecuteNonQueryAsync(DbConnection connection, string commandText, params (string Name, object Value)[] parameters)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = commandText;

		foreach (var (name, value) in parameters) {
			AddParameter(command, name, value);
		}

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<IReadOnlyList<string>> ReadRoleNamesAsync(DbConnection connection)
	{
		var names = new List<string>();

		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM identity_role ORDER BY id;";

		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			names.Add(reader.GetString(0));
		}

		return names;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
