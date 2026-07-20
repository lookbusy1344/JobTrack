namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-AUDIT-001 contract for schema slice 12: append-only
///     <c>audit_event</c> storage (impl plan §6.2 item 12, spec §16), asserted
///     identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlAuditEventSchemaTests" /> and
///     <see cref="SqliteAuditEventSchemaTests" />.
/// </summary>
public abstract class AuditEventSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected AuditEventSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_audit_event_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection)).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_a_minimal_audit_event_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertAuditEventAsync(
			connection, userId, "CreateJobNode", "JobNode", 1, Guid.NewGuid(), null, null, null);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Inserting_an_audit_event_with_before_and_after_payloads_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertAuditEventAsync(
			connection,
			userId,
			"CorrectWorkSession",
			"WorkSession",
			7,
			Guid.NewGuid(),
			"Worker forgot to clock out",
			"""{"finishedAt":null}""",
			"""{"finishedAt":"2026-01-05T17:00:00Z"}""");

		id.Should().BePositive();
	}

	/// <summary>
	///     Fresh-eyes review §2.6: an unknown-subject authentication failure has no real actor to
	///     attribute the event to, so <c>actor_user_id</c> must accept <see langword="null" /> rather
	///     than forcing a fabricated/reused "system" <c>app_user</c> row keyed by ordinary display-name
	///     data.
	/// </summary>
	[Fact]
	public async Task Inserting_an_audit_event_with_a_null_actor_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var id = await InsertAuditEventAsync(
			connection, null, "authentication.login-failed", "authentication_attempt", 0, Guid.NewGuid(), null, null,
			"""{"subject":"redacted"}""");

		id.Should().BePositive();
	}

	[Fact]
	public async Task Inserting_an_audit_event_with_a_blank_operation_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertAuditEventAsync(
			connection, userId, "   ", "JobNode", 1, Guid.NewGuid(), null, null, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_an_audit_event_with_a_blank_entity_type_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertAuditEventAsync(
			connection, userId, "CreateJobNode", "  ", 1, Guid.NewGuid(), null, null, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Updating_an_existing_audit_event_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var id = await InsertAuditEventAsync(
			connection, userId, "CreateJobNode", "JobNode", 1, Guid.NewGuid(), null, null, null);

		var act = async () => await UpdateAuditEventReasonAsync(connection, id, "Rewritten");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_an_existing_audit_event_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var id = await InsertAuditEventAsync(
			connection, userId, "CreateJobNode", "JobNode", 1, Guid.NewGuid(), null, null, null);

		var act = async () => await DeleteAuditEventAsync(connection, id);

		await act.Should().ThrowAsync<DbException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	protected abstract Task<long> InsertAuditEventAsync(
		DbConnection connection,
		long? actorUserId,
		string operation,
		string entityType,
		long entityId,
		Guid correlationId,
		string? reason,
		string? beforeData,
		string? afterData);

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

	private static async Task<(long UserId, long IdentityUserId)> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		return (appUserId, 0);
	}

	private static async Task UpdateAuditEventReasonAsync(DbConnection connection, long id, string reason)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE audit_event SET reason = @reason WHERE id = @id;";
		AddParameter(command, "@reason", reason);
		AddParameter(command, "@id", id);

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task DeleteAuditEventAsync(DbConnection connection, long id)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM audit_event WHERE id = @id;";
		AddParameter(command, "@id", id);

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> CountRowsAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM audit_event;";
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
