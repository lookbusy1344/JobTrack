namespace JobTrack.AdminCli.Tests;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Real, schema-deployed database tests for <see cref="IssueTokenCommand" /> — the <c>issue-token</c>
///     CLI command mints a bearer personal access token for an existing employee without going through
///     the browser-only self-service page, for scripting/tooling (e.g. hurl-driven API smoke tests)
///     that need a real credential without a signed-in session.
/// </summary>
public sealed class IssueTokenCommandTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	[Fact]
	public async Task Issues_a_usable_bearer_token_for_an_existing_employee_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var appUserId = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.token");

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var jobTrackClient = JobTrackSqlite.Create(database.ConnectionString);
			var console = new FakeConsoleIO([], []);

			var exitCode = await IssueTokenCommand.RunAsync(
				console, userManager, jobTrackClient, "ada.token", "hurl-smoke", Duration.FromDays(1), CancellationToken.None);

			exitCode.Should().Be(0);
			var token = ExtractToken(console);

			var authenticated = await jobTrackClient.Tokens.TryAuthenticateAsync(
				new() { Token = token });
			authenticated.Should().NotBeNull();
			authenticated!.UserId.Should().Be(new AppUserId(appUserId));
			console.Errors.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Issues_a_usable_bearer_token_for_an_existing_employee_on_postgresql()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.PostgreSql, database.ConnectionString);
			var appUserId = await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.PostgreSql, "ada.token");

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentityPostgreSql(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var jobTrackClient = JobTrackPostgreSql.Create(new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build());
			var console = new FakeConsoleIO([], []);

			var exitCode = await IssueTokenCommand.RunAsync(
				console, userManager, jobTrackClient, "ada.token", "hurl-smoke", Duration.FromDays(1), CancellationToken.None);

			exitCode.Should().Be(0);
			var token = ExtractToken(console);

			var authenticated = await jobTrackClient.Tokens.TryAuthenticateAsync(
				new() { Token = token });
			authenticated.Should().NotBeNull();
			authenticated!.UserId.Should().Be(new AppUserId(appUserId));
			console.Errors.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_without_issuing_anything_for_an_unknown_username()
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
			var jobTrackClient = JobTrackSqlite.Create(database.ConnectionString);
			var console = new FakeConsoleIO([], []);

			var exitCode = await IssueTokenCommand.RunAsync(
				console, userManager, jobTrackClient, "no.such.user", "hurl-smoke", Duration.FromDays(1), CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("no.such.user", StringComparison.Ordinal));
			console.Lines.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_when_the_requested_lifetime_exceeds_the_domain_policy_maximum()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);
			await SeedEmployeeAsync(database.ConnectionString, SchemaProvider.Sqlite, "ada.token-too-long");

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var jobTrackClient = JobTrackSqlite.Create(database.ConnectionString);
			var console = new FakeConsoleIO([], []);

			var exitCode = await IssueTokenCommand.RunAsync(
				console, userManager, jobTrackClient, "ada.token-too-long", "hurl-smoke", Duration.FromDays(366), CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle();
			console.Lines.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static string ExtractToken(FakeConsoleIO console)
	{
		var line = console.Lines.Should().ContainSingle(l => l.StartsWith("Personal access token for", StringComparison.Ordinal)).Which;
		var separatorIndex = line.LastIndexOf(": ", StringComparison.Ordinal);
		return line[(separatorIndex + 2)..];
	}

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

	private static async Task<long> SeedEmployeeAsync(string connectionString, SchemaProvider provider, string userName)
	{
		await using var connection = provider == SchemaProvider.PostgreSql
			? (DbConnection)new NpgsqlConnection(connectionString)
			: new SqliteConnection(connectionString);
		await connection.OpenAsync();

		if (provider == SchemaProvider.Sqlite) {
			await using var pragma = connection.CreateCommand();
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var prefix = provider == SchemaProvider.PostgreSql ? "@" : "$";

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = provider == SchemaProvider.PostgreSql
			? "INSERT INTO app_user (display_name, iana_time_zone) VALUES (@displayName, 'Europe/London') RETURNING id;"
			: "INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'Europe/London'); SELECT last_insert_rowid();";
		AddParameter(appUserCommand, prefix + "displayName", userName);
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
		AddParameter(identityUserCommand, prefix + "appUserId", appUserId);
		AddParameter(identityUserCommand, prefix + "userName", userName);
		AddParameter(identityUserCommand, prefix + "normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, prefix + "passwordHash", passwordHash);
		AddParameter(identityUserCommand, prefix + "securityStamp", placeholderUser.SecurityStamp);
		AddParameter(identityUserCommand, prefix + "concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = await identityUserCommand.ExecuteScalarAsync();

		return appUserId;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		_ = command.Parameters.Add(parameter);
	}
}
