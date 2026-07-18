namespace JobTrack.Identity.Tests;

using System.Security.Claims;
using Abstractions;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TestSupport;

/// <summary>
///     The base <see cref="UserClaimsPrincipalFactory{TUser}" /> never queries role membership -- only
///     the two-type-parameter overload with a <c>RoleManager&lt;TRole&gt;</c> does, and this project
///     deliberately has no generic Identity role type (ADR 0022). Without
///     <see cref="JobTrackUserClaimsPrincipalFactory" />, a signed-in employee's cookie principal would
///     carry no role claims at all, and every <c>[Authorize(Policy = ...)]</c> check (plan §8.3) would
///     fail regardless of the employee's actual roles.
/// </summary>
public sealed class JobTrackUserClaimsPrincipalFactoryTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly SqliteDatabaseFixture database = new();

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();
	}

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task The_generated_principal_carries_a_role_claim_for_each_assigned_role()
	{
		var appUserId = await InsertAppUserAsync("Ada Lovelace");
		var identityUserId = await InsertIdentityUserAsync(appUserId, "ada.claims");
		await AssignRoleAsync(identityUserId, EmployeeRole.Administrator);
		await AssignRoleAsync(identityUserId, EmployeeRole.CostViewer);

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<JobTrackIdentityUser>>();

		var principal = await factory.CreateAsync((await userManager.FindByNameAsync("ADA.CLAIMS"))!);

		var roleClaimValues = principal.FindAll(ClaimTypes.Role).Select(c => c.Value);
		roleClaimValues.Should().BeEquivalentTo("Administrator", "Cost viewer");
	}

	[Fact]
	public async Task An_employee_with_no_roles_gets_a_principal_with_no_role_claims()
	{
		var appUserId = await InsertAppUserAsync("Grace Hopper");
		_ = await InsertIdentityUserAsync(appUserId, "grace.claims");

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<JobTrackIdentityUser>>();

		var principal = await factory.CreateAsync((await userManager.FindByNameAsync("GRACE.CLAIMS"))!);

		principal.FindAll(ClaimTypes.Role).Should().BeEmpty();
	}

	private async Task<AppUserId> InsertAppUserAsync(string displayName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = command.Parameters.AddWithValue("$displayName", displayName);
		var id = (long)(await command.ExecuteScalarAsync())!;
		return new(id);
	}

	private async Task<long> InsertIdentityUserAsync(AppUserId appUserId, string userName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO identity_user
							  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
							  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
							  VALUES
							  	($appUserId, $userName, $normalizedUserName, 'hash', 'stamp', 'cstamp', 0, 1, 1, 0);
							  SELECT last_insert_rowid();
							  """;
		_ = command.Parameters.AddWithValue("$appUserId", appUserId.Value);
		_ = command.Parameters.AddWithValue("$userName", userName);
		_ = command.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private async Task AssignRoleAsync(long identityUserId, EmployeeRole role)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ($identityUserId, $roleId);";
		_ = command.Parameters.AddWithValue("$identityUserId", identityUserId);
		_ = command.Parameters.AddWithValue("$roleId", (short)role);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
