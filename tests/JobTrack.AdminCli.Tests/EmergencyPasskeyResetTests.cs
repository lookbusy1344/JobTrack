namespace JobTrack.AdminCli.Tests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Real, schema-deployed database tests for <see cref="EmergencyPasskeyReset" /> (ADR 0071 §8.4),
///     mirroring <see cref="EmergencyTwoFactorResetTests" />'s shape. The reset removes passkeys only:
///     the password and any TOTP enrolment stay intact; the security stamp rotates and personal access
///     tokens are revoked.
/// </summary>
public sealed class EmergencyPasskeyResetTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	[Fact]
	public async Task Reset_removes_all_passkeys_and_audits_it_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var (appUserId, identityUserId) = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk");
			await SeedPasskeyAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId, "Work MacBook", [1, 2, 3, 4]);
			await SeedPasskeyAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId, "Blue YubiKey", [5, 6, 7, 8]);
			var stampBefore = await GetSecurityStampAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId);

			var (exitCode, console) = await RunResetAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk");

			exitCode.Should().Be(0);
			(await GetPasskeyCountAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId)).Should().Be(0);

			var reloaded = await FindUserAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk");
			reloaded!.SecurityStamp.Should().NotBe(stampBefore);

			var auditRow = await GetLatestAuditEventAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId);
			auditRow.Operation.Should().Be("emergency-passkey-reset");
			auditRow.ActorUserId.Should().Be(appUserId);

			console.Errors.Should().BeEmpty();
			console.Lines.Should().ContainSingle(line => line.Contains("2 passkeys", StringComparison.Ordinal) && line.Contains("ada.reset-pk", StringComparison.Ordinal));
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Reset_removes_all_passkeys_and_audits_it_on_postgresql()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.PostgreSql, database.ConnectionString);
			var (appUserId, identityUserId) = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.PostgreSql, "ada.reset-pk");
			await SeedPasskeyAsync(database.ConnectionString, SchemaProvider.PostgreSql, identityUserId, "Work MacBook", [1, 2, 3, 4]);
			var stampBefore = await GetSecurityStampAsync(database.ConnectionString, SchemaProvider.PostgreSql, identityUserId);

			var (exitCode, console) = await RunResetAsync(database.ConnectionString, SchemaProvider.PostgreSql, "ada.reset-pk");

			exitCode.Should().Be(0);
			(await GetPasskeyCountAsync(database.ConnectionString, SchemaProvider.PostgreSql, identityUserId)).Should().Be(0);

			var reloaded = await FindUserAsync(database.ConnectionString, SchemaProvider.PostgreSql, "ada.reset-pk");
			reloaded!.SecurityStamp.Should().NotBe(stampBefore);

			var auditRow = await GetLatestAuditEventAsync(database.ConnectionString, SchemaProvider.PostgreSql, identityUserId);
			auditRow.Operation.Should().Be("emergency-passkey-reset");
			auditRow.ActorUserId.Should().Be(appUserId);

			console.Errors.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Reset_leaves_the_password_and_two_factor_enrolment_untouched_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var (_, identityUserId) = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk-keeps");
			await SeedPasskeyAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId, "Work MacBook", [1, 2, 3, 4]);
			await SeedTwoFactorEnabledAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId);
			var passwordHashBefore = await GetPasswordHashAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId);

			var (exitCode, _) = await RunResetAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk-keeps");

			exitCode.Should().Be(0);
			var reloaded = await FindUserAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk-keeps");
			reloaded!.TwoFactorEnabled.Should().BeTrue("reset-passkeys must not disable TOTP");
			reloaded.AuthenticatorKeyProtected.Should().NotBeNull();
			(await GetPasswordHashAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId))
				.Should().Be(passwordHashBefore, "reset-passkeys must not change the password");
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Reset_revokes_the_employees_personal_access_tokens_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var (appUserId, identityUserId) = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk-revokes");
			await SeedPasskeyAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId, "Work MacBook", [1, 2, 3, 4]);
			var jobTrackClient = JobTrackSqlite.Create(database.ConnectionString);
			var issued = await jobTrackClient.Tokens.IssueAsync(new() {
				Context = new() {
					Actor = new(appUserId),
					CorrelationId = Guid.NewGuid(),
				},
				TargetUserId = new(appUserId),
				Label = "cli-test-token",
				ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
			});

			var (exitCode, _) = await RunResetAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-pk-revokes");

			exitCode.Should().Be(0);
			var authenticated = await jobTrackClient.Tokens.TryAuthenticateAsync(new() {
				Token = issued.Token,
			});
			authenticated.Should().BeNull("the emergency passkey reset must revoke every personal access token for the reset employee");
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Reset_for_an_unknown_username_fails_without_changing_any_state()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var (exitCode, console) = await RunResetAsync(database.ConnectionString, SchemaProvider.Sqlite, "no.such.user");

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("no.such.user", StringComparison.Ordinal));
			console.Lines.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static async Task<(int ExitCode, FakeConsoleIO Console)> RunResetAsync(
		string connectionString, SchemaProvider provider, string username)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = provider == SchemaProvider.PostgreSql
			? services.AddJobTrackIdentityPostgreSql(connectionString)
			: services.AddJobTrackIdentitySqlite(connectionString);
		await using var built = services.BuildServiceProvider();
		using var scope = built.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var identityContext = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();
		var console = new FakeConsoleIO([], []);

		var cliProvider = provider == SchemaProvider.PostgreSql ? AdminCliProvider.PostgreSql : AdminCliProvider.Sqlite;
		var exitCode = await EmergencyPasskeyReset.RunAsync(
			console, userManager, identityContext, cliProvider, username, SystemClock.Instance, CancellationToken.None);
		return (exitCode, console);
	}

	private static async Task<JobTrackIdentityUser?> FindUserAsync(string connectionString, SchemaProvider provider, string username)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = provider == SchemaProvider.PostgreSql
			? services.AddJobTrackIdentityPostgreSql(connectionString)
			: services.AddJobTrackIdentitySqlite(connectionString);
		await using var built = services.BuildServiceProvider();
		using var scope = built.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		return await userManager.FindByNameAsync(username);
	}

	private static async Task DeploySchemaAsync(SchemaProvider provider, string connectionString)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(provider));
		var deployer = new SchemaDeployer(
			connection,
			provider == SchemaProvider.PostgreSql ? new PostgreSqlSchemaVersionStore() : new SqliteSchemaVersionStore(),
			provider == SchemaProvider.PostgreSql ? new PostgreSqlDeploymentLockStrategy() : new SqliteDeploymentLockStrategy(),
			ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
		await PostgreSqlTestInfrastructure.EnsureSecurityDefinerFunctionsAsync(connection, provider);
	}

	private static async Task<(long AppUserId, long IdentityUserId)> SeedEmployeeAsync(
		string connectionString, SchemaProvider provider, string userName)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = provider == SchemaProvider.PostgreSql
			? "INSERT INTO app_user (display_name, iana_time_zone) VALUES (@displayName, 'Europe/London') RETURNING id;"
			: "INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'Europe/London'); SELECT last_insert_rowid();";
		AddParameter(appUserCommand, Prefix(provider) + "displayName", userName);
		var appUserId = Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		var placeholderUser = new JobTrackIdentityUser {
			AppUserId = new(appUserId),
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = string.Empty,
			SecurityStamp = Guid.NewGuid().ToString(),
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(placeholderUser, KnownPassword);

		await using var identityUserCommand = connection.CreateCommand();
		identityUserCommand.CommandText = provider == SchemaProvider.PostgreSql
			? """
			  INSERT INTO identity_user
			  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
			  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
			  VALUES
			  	(@appUserId, @userName, @normalizedUserName, @passwordHash, @securityStamp, @concurrencyStamp, false, true, true, 0)
			  RETURNING id;
			  """
			: """
			  INSERT INTO identity_user
			  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
			  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
			  VALUES
			  	($appUserId, $userName, $normalizedUserName, $passwordHash, $securityStamp, $concurrencyStamp, 0, 1, 1, 0);
			  SELECT last_insert_rowid();
			  """;
		var p = Prefix(provider);
		AddParameter(identityUserCommand, p + "appUserId", appUserId);
		AddParameter(identityUserCommand, p + "userName", userName);
		AddParameter(identityUserCommand, p + "normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, p + "passwordHash", passwordHash);
		AddParameter(identityUserCommand, p + "securityStamp", placeholderUser.SecurityStamp);
		AddParameter(identityUserCommand, p + "concurrencyStamp", placeholderUser.ConcurrencyStamp);
		var identityUserId = Convert.ToInt64(await identityUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		return (appUserId, identityUserId);
	}

	private static async Task SeedPasskeyAsync(
		string connectionString, SchemaProvider provider, long identityUserId, string name, byte[] credentialId)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		await using var command = connection.CreateCommand();
		var p = Prefix(provider);
		command.CommandText = provider == SchemaProvider.PostgreSql
			? $"""
			   INSERT INTO identity_user_passkey
			   	(credential_id, identity_user_id, name, normalized_name, public_key, sign_count,
			   	 is_user_verified, is_backup_eligible, is_backed_up, aaguid, attestation_object, client_data_json)
			   VALUES
			   	({p}credentialId, {p}identityUserId, {p}name, {p}normalizedName, {p}publicKey, 0,
			   	 true, false, false, {p}aaguid, {p}attestation, {p}clientData);
			   """
			: $"""
			   INSERT INTO identity_user_passkey
			   	(credential_id, identity_user_id, name, normalized_name, public_key, created_at, sign_count,
			   	 is_user_verified, is_backup_eligible, is_backed_up, aaguid, attestation_object, client_data_json, row_version)
			   VALUES
			   	({p}credentialId, {p}identityUserId, {p}name, {p}normalizedName, {p}publicKey, 0, 0,
			   	 1, 0, 0, {p}aaguid, {p}attestation, {p}clientData, 1);
			   """;
		AddParameter(command, p + "credentialId", credentialId);
		AddParameter(command, p + "identityUserId", identityUserId);
		AddParameter(command, p + "name", name);
		AddParameter(command, p + "normalizedName", name.ToUpperInvariant());
		AddParameter(command, p + "publicKey", new byte[] {
			1, 2, 3, 4,
		});
		AddParameter(command, p + "aaguid", new byte[16]);
		AddParameter(command, p + "attestation", new byte[] {
			1,
		});
		AddParameter(command, p + "clientData", new byte[] {
			1,
		});
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task SeedTwoFactorEnabledAsync(string connectionString, SchemaProvider provider, long identityUserId)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		await using var command = connection.CreateCommand();
		var p = Prefix(provider);
		command.CommandText =
			$"UPDATE identity_user SET two_factor_enabled = {p}enabled, authenticator_key_protected = {p}key WHERE id = {p}id;";
		AddParameter(command, p + "enabled", true);
		AddParameter(command, p + "key", new byte[] {
			1, 2, 3,
		});
		AddParameter(command, p + "id", identityUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<int> GetPasskeyCountAsync(string connectionString, SchemaProvider provider, long identityUserId)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT count(*) FROM identity_user_passkey WHERE identity_user_id = {Prefix(provider)}id;";
		AddParameter(command, Prefix(provider) + "id", identityUserId);
		return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<string> GetSecurityStampAsync(string connectionString, SchemaProvider provider, long identityUserId)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT security_stamp FROM identity_user WHERE id = {Prefix(provider)}id;";
		AddParameter(command, Prefix(provider) + "id", identityUserId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<string> GetPasswordHashAsync(string connectionString, SchemaProvider provider, long identityUserId)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT password_hash FROM identity_user WHERE id = {Prefix(provider)}id;";
		AddParameter(command, Prefix(provider) + "id", identityUserId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<(string Operation, long ActorUserId)> GetLatestAuditEventAsync(
		string connectionString, SchemaProvider provider, long identityUserId)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		await using var command = connection.CreateCommand();
		command.CommandText =
			$"SELECT operation, actor_user_id FROM audit_event WHERE entity_type = 'identity_user' AND entity_id = {Prefix(provider)}entityId " +
			"ORDER BY id DESC LIMIT 1;";
		AddParameter(command, Prefix(provider) + "entityId", identityUserId);

		await using var reader = await command.ExecuteReaderAsync();
		var hasRow = await reader.ReadAsync();
		hasRow.Should().BeTrue("an audit_event row should have been written");

		return (reader.GetString(0), reader.GetInt64(1));
	}

	private static async Task<DbConnection> OpenConnectionAsync(string connectionString, SchemaProvider provider)
	{
		DbConnection connection = provider switch {
			SchemaProvider.PostgreSql => new NpgsqlConnection(connectionString),
			SchemaProvider.Sqlite => new SqliteConnection(connectionString),
			_ => throw new ArgumentOutOfRangeException(nameof(provider)),
		};
		await connection.OpenAsync();

		if (provider == SchemaProvider.Sqlite) {
			await using var pragma = connection.CreateCommand();
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		return connection;
	}

	private static string Prefix(SchemaProvider provider) => provider == SchemaProvider.PostgreSql ? "@" : "$";

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
