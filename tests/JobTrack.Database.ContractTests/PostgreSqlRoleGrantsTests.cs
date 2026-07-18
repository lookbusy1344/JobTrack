namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     TC-DB-ROLES-001: PostgreSQL-only contract for the roles-and-grants
///     script (impl plan §6.1, §6.7 gate item "role grants prove the normal
///     application role cannot perform DDL, erase audit rows, or delete
///     retained history"). No SQLite equivalent -- SQLite has no roles or
///     GRANT concept.
///     Every negative assertion is exercised via <c>SET ROLE</c> on the same
///     admin connection used to deploy the schema, rather than a separate
///     authenticated connection per role: the local/CI admin account is a
///     superuser, which may <c>SET ROLE</c> to any role without needing prior
///     membership or a password, so this needs no pg_hba.conf changes or
///     environment-specific login credentials to prove the grants hold.
/// </summary>
public sealed class PostgreSqlRoleGrantsTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task The_application_role_cannot_create_a_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_application", "CREATE TABLE rogue_table (id integer);");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_application_role_cannot_alter_an_existing_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_application", "ALTER TABLE app_user ADD COLUMN rogue_column text;");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_application_role_cannot_delete_audit_event_rows()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertAuditEventAsync(connection, userId);

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_application", "DELETE FROM audit_event;");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		(await CountRowsAsync(connection, "audit_event")).Should().Be(1);
	}

	[Fact]
	public async Task The_application_role_cannot_update_audit_event_rows()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertAuditEventAsync(connection, userId);

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_application", "UPDATE audit_event SET reason = 'tampered';");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_application_role_cannot_delete_retained_work_session_history()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertWorkSessionAsync(connection, leafWorkId, userId);

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_application", "DELETE FROM work_session;");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		(await CountRowsAsync(connection, "work_session")).Should().Be(1);
	}

	[Fact]
	public async Task The_application_role_can_still_read_and_write_ordinary_tables()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_application",
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ('Bob Example', 'Europe/London');");

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_readonly_role_can_select_but_cannot_insert()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		await SeedAppUserAsync(connection, "Alice Example");

		var selectAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT COUNT(*) FROM app_user;");
		var insertAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "INSERT INTO app_user (display_name, iana_time_zone) VALUES ('Carol Example', 'Europe/London');");

		await selectAct.Should().NotThrowAsync();
		await insertAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_readonly_role_cannot_select_identity_secret_columns()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, userId);

		var passwordHashAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT password_hash FROM identity_user;");
		var securityStampAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT security_stamp FROM identity_user;");
		var concurrencyStampAct = async () =>
			await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT concurrency_stamp FROM identity_user;");
		var userNameAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT user_name FROM identity_user;");

		await passwordHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await securityStampAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await concurrencyStampAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await userNameAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_readonly_role_cannot_select_the_totp_key_but_can_read_the_two_factor_enabled_flag()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, userId);

		var authenticatorKeyAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "SELECT authenticator_key_protected FROM identity_user;");
		var twoFactorEnabledAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "SELECT two_factor_enabled, two_factor_enabled_at FROM identity_user;");

		await authenticatorKeyAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await twoFactorEnabledAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_readonly_role_cannot_select_the_personal_access_token_hash_but_can_read_reporting_columns()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertPersonalAccessTokenAsync(connection, userId);

		var tokenHashAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT token_hash FROM personal_access_token;");
		var labelAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT label FROM personal_access_token;");
		var expiryAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "SELECT created_at, expires_at, revoked_at, last_used_at FROM personal_access_token;");

		await tokenHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await labelAct.Should().NotThrowAsync();
		await expiryAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_emergency_reset_role_can_revoke_a_token_but_cannot_issue_one_or_assign_roles()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);
		var tokenId = await InsertPersonalAccessTokenAsync(connection, userId);

		var revokeAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE personal_access_token SET revoked_at = now() WHERE id = {tokenId};");
		var issueAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_emergency_reset",
			"INSERT INTO personal_access_token (app_user_id, token_hash, label, expires_at) " +
			$"VALUES ({userId}, 'rogue-hash', 'rogue', now() + interval '1 day');");
		var assignRoleAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_emergency_reset",
			$"INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ({identityUserId}, 1);");
		var selectTokenHashAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", "SELECT token_hash FROM personal_access_token;");
		var changeTokenHashAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE personal_access_token SET token_hash = 'known-rogue-hash' WHERE id = {tokenId};");
		var reassignTokenAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE personal_access_token SET app_user_id = {userId} WHERE id = {tokenId};");
		var extendTokenAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset",
			$"UPDATE personal_access_token SET expires_at = now() + interval '30 days' WHERE id = {tokenId};");

		await revokeAct.Should().NotThrowAsync();
		await issueAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await assignRoleAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await selectTokenHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await changeTokenHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await reassignTokenAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await extendTokenAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_emergency_reset_role_can_update_identity_user_but_not_job_node()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);

		var updateIdentityAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE identity_user SET security_stamp = 'reset' WHERE id = {identityUserId};");
		var insertJobNodeAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_emergency_reset",
			$"INSERT INTO job_node (description, posted_by_user_id, owner_user_id, priority_id, posted_at) " +
			$"VALUES ('Rogue', {userId}, {userId}, {PriorityMedium}, now());");

		await updateIdentityAct.Should().NotThrowAsync();
		await insertJobNodeAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	private async Task<NpgsqlConnection> OpenDeployedConnectionAsync()
	{
		var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlRolesAndGrantsScriptPath(), CancellationToken.None);

		return connection;
	}

	private static async Task ExecuteAsRoleAsync(NpgsqlConnection connection, string role, string commandText)
	{
		await using var setRole = connection.CreateCommand();
		setRole.CommandText = $"SET ROLE {role};";
		_ = await setRole.ExecuteNonQueryAsync();

		try {
			await using var command = connection.CreateCommand();
			command.CommandText = commandText;
			_ = await command.ExecuteNonQueryAsync();
		}
		finally {
			await using var resetRole = connection.CreateCommand();
			resetRole.CommandText = "RESET ROLE;";
			_ = await resetRole.ExecuteNonQueryAsync();
		}
	}

	private static async Task<(long AppUserId, long IdentityUserId)> SeedAppUserAsync(DbConnection connection, string displayName)
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

	private static async Task<long> InsertIdentityUserAsync(DbConnection connection, long appUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO identity_user
							  (app_user_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp)
							  VALUES (@appUserId, @userName, @normalizedUserName, 'hash', 'stamp', 'concurrency')
							  RETURNING id;
							  """;
		AddParameter(command, "@appUserId", appUserId);
		AddParameter(command, "@userName", $"user{appUserId}");
		AddParameter(command, "@normalizedUserName", $"USER{appUserId}");

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<long> InsertPersonalAccessTokenAsync(DbConnection connection, long appUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO personal_access_token (app_user_id, token_hash, label, expires_at)
							  VALUES (@appUserId, @tokenHash, 'test-token', now() + interval '1 day')
							  RETURNING id;
							  """;
		AddParameter(command, "@appUserId", appUserId);
		AddParameter(command, "@tokenHash", $"hash-{appUserId}-{Guid.NewGuid():N}");

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<(long UserId, long LeafWorkId)> SeedUserAndLeafWorkAsync(DbConnection connection, string displayName)
	{
		var (userId, _) = await SeedAppUserAsync(connection, displayName);
		var rootId = await InsertNodeAsync(connection, userId, null);
		var leafId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, leafId);
		return (userId, leafId);
	}

	private static async Task<long> InsertNodeAsync(DbConnection connection, long ownerUserId, long? parentId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @ownerUserId, @ownerUserId, @priorityId, now())
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", "A job");
		AddParameter(command, "@ownerUserId", ownerUserId);
		AddParameter(command, "@priorityId", PriorityMedium);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task InsertLeafWorkAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO leaf_work (job_node_id, changed_at) VALUES (@jobNodeId, now());";
		AddParameter(command, "@jobNodeId", jobNodeId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task InsertWorkSessionAsync(DbConnection connection, long leafWorkId, long workedByUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  VALUES (@leafWorkId, @workedByUserId, now() - interval '1 hour', now(), now());
							  """;
		AddParameter(command, "@leafWorkId", leafWorkId);
		AddParameter(command, "@workedByUserId", workedByUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task InsertAuditEventAsync(DbConnection connection, long actorUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id)
							  VALUES (@actorUserId, 'Test', 'app_user', @actorUserId, gen_random_uuid());
							  """;
		AddParameter(command, "@actorUserId", actorUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> CountRowsAsync(DbConnection connection, string tableName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
