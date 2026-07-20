namespace JobTrack.AdminCli.Tests;

using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
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
///     Real, schema-deployed database tests for <see cref="EmergencyPasswordReset" /> — unlike
///     <see cref="BootstrapCommand" />, this is new logic (spec §7.1, plan §8.6), not a thin wrapper
///     over something already tested elsewhere.
/// </summary>
public sealed partial class EmergencyPasswordResetTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	[Fact]
	public async Task Reset_generates_a_working_temporary_credential_and_audits_it_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var (appUserId, identityUserId) = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset");
			var stampBefore = await GetSecurityStampAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var identityContext = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();
			var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<JobTrackIdentityUser>>();
			var console = new FakeConsoleIO([], []);

			var exitCode = await EmergencyPasswordReset.RunAsync(
				console, userManager, identityContext, passwordHasher, AdminCliProvider.Sqlite, "ada.reset", SystemClock.Instance,
				CancellationToken.None);

			exitCode.Should().Be(0);
			var temporaryPassword = ExtractTemporaryPassword(console);

			var reloaded = await userManager.FindByNameAsync("ada.reset");
			reloaded.Should().NotBeNull();
			(await userManager.CheckPasswordAsync(reloaded!, temporaryPassword)).Should().BeTrue();
			(await userManager.CheckPasswordAsync(reloaded!, KnownPassword)).Should().BeFalse();
			reloaded!.RequiresPasswordChange.Should().BeTrue();
			reloaded.SecurityStamp.Should().NotBe(stampBefore);

			var auditRow = await GetLatestAuditEventAsync(database.ConnectionString, SchemaProvider.Sqlite, identityUserId);
			auditRow.Operation.Should().Be("emergency-password-reset");
			auditRow.ActorUserId.Should().Be(appUserId);

			console.Errors.Should().BeEmpty();
			console.Lines.Should().ContainSingle(line => line.Contains(temporaryPassword, StringComparison.Ordinal));
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Reset_generates_a_working_temporary_credential_and_audits_it_on_postgresql()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.PostgreSql, database.ConnectionString);
			var (appUserId, identityUserId) = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.PostgreSql, "ada.reset");
			var stampBefore = await GetSecurityStampAsync(database.ConnectionString, SchemaProvider.PostgreSql, identityUserId);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentityPostgreSql(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var identityContext = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();
			var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<JobTrackIdentityUser>>();
			var console = new FakeConsoleIO([], []);

			var exitCode = await EmergencyPasswordReset.RunAsync(
				console, userManager, identityContext, passwordHasher, AdminCliProvider.PostgreSql, "ada.reset", SystemClock.Instance,
				CancellationToken.None);

			exitCode.Should().Be(0);
			var temporaryPassword = ExtractTemporaryPassword(console);

			var reloaded = await userManager.FindByNameAsync("ada.reset");
			reloaded.Should().NotBeNull();
			(await userManager.CheckPasswordAsync(reloaded!, temporaryPassword)).Should().BeTrue();
			(await userManager.CheckPasswordAsync(reloaded!, KnownPassword)).Should().BeFalse();
			reloaded!.RequiresPasswordChange.Should().BeTrue();
			reloaded.SecurityStamp.Should().NotBe(stampBefore);

			var auditRow = await GetLatestAuditEventAsync(database.ConnectionString, SchemaProvider.PostgreSql, identityUserId);
			auditRow.Operation.Should().Be("emergency-password-reset");
			auditRow.ActorUserId.Should().Be(appUserId);

			console.Errors.Should().BeEmpty();
			console.Lines.Should().ContainSingle(line => line.Contains(temporaryPassword, StringComparison.Ordinal));
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Reset_revokes_the_employees_personal_access_tokens_on_sqlite()
	{
		// ADR 0029: a PAT is a credential of the same sensitivity class as a session cookie, so it
		// gets the same revocation triggers as a security-stamp rotation -- emergency reset included.
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var (appUserId, _) = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.reset-revokes");
			var jobTrackClient = JobTrackSqlite.Create(database.ConnectionString);
			var issued = await jobTrackClient.Tokens.IssueAsync(new() {
				Context = new() { Actor = new(appUserId), CorrelationId = Guid.NewGuid() },
				TargetUserId = new(appUserId),
				Label = "cli-test-token",
				ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
			});

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var identityContext = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();
			var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<JobTrackIdentityUser>>();
			var console = new FakeConsoleIO([], []);

			var exitCode = await EmergencyPasswordReset.RunAsync(
				console, userManager, identityContext, passwordHasher, AdminCliProvider.Sqlite, "ada.reset-revokes", SystemClock.Instance,
				CancellationToken.None);

			exitCode.Should().Be(0);
			var authenticated = await jobTrackClient.Tokens.TryAuthenticateAsync(
				new() { Token = issued.Token });
			authenticated.Should().BeNull("the emergency reset must revoke every personal access token for the reset employee");
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

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var identityContext = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();
			var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<JobTrackIdentityUser>>();
			var console = new FakeConsoleIO([], []);

			var exitCode = await EmergencyPasswordReset.RunAsync(
				console, userManager, identityContext, passwordHasher, AdminCliProvider.Sqlite, "no.such.user", SystemClock.Instance,
				CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("no.such.user", StringComparison.Ordinal));
			console.Lines.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static string ExtractTemporaryPassword(FakeConsoleIO console)
	{
		var line = console.Lines.Should().ContainSingle(l => l.StartsWith("Temporary password for", StringComparison.Ordinal)).Which;
		return TemporaryPasswordPattern().Match(line) is { Success: true } match
			? match.Groups["password"].Value
			: throw new InvalidOperationException($"Could not extract temporary password from: {line}");
	}

	[GeneratedRegex(@": (?<password>\S+)$")]
	private static partial Regex TemporaryPasswordPattern();

	private static async Task DeploySchemaAsync(SchemaProvider provider, string connectionString)
	{
		DbConnection connection = provider switch {
			SchemaProvider.PostgreSql => new NpgsqlConnection(connectionString),
			SchemaProvider.Sqlite => new SqliteConnection(connectionString),
			_ => throw new ArgumentOutOfRangeException(nameof(provider)),
		};
		await using var ownedConnection = connection;
		await connection.OpenAsync();

		if (provider == SchemaProvider.Sqlite) {
			await using var pragma = connection.CreateCommand();
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(provider));
		var deployer = new SchemaDeployer(
			connection,
			provider == SchemaProvider.PostgreSql ? new PostgreSqlSchemaVersionStore() : new SqliteSchemaVersionStore(),
			provider == SchemaProvider.PostgreSql ? new PostgreSqlDeploymentLockStrategy() : new SqliteDeploymentLockStrategy(),
			ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
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

		var placeholderHasher = new PasswordHasher<JobTrackIdentityUser>();
		var placeholderUser = new JobTrackIdentityUser {
			AppUserId = new(appUserId),
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = string.Empty,
			SecurityStamp = Guid.NewGuid().ToString(),
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var passwordHash = placeholderHasher.HashPassword(placeholderUser, KnownPassword);

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

	private static async Task<string> GetSecurityStampAsync(string connectionString, SchemaProvider provider, long identityUserId)
	{
		await using var connection = await OpenConnectionAsync(connectionString, provider);
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT security_stamp FROM identity_user WHERE id = {Prefix(provider)}id;";
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
