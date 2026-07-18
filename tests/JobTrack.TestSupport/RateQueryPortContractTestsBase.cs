namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="IRateQueryPort" /> (plan §8.5 slice 7), asserted identically
///     against PostgreSQL and SQLite by one thin sealed subclass per provider's own test project --
///     same shape as <see cref="ScheduleQueryPortContractTestsBase" />. Seeds a user cost rate and a
///     node rate override via the real <see cref="IInstallationBootstrapPort" />/<see cref="IRateCommandPort" />.
/// </summary>
public abstract class RateQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected RateQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetRatesAsync_returns_the_employees_cost_rates_and_node_overrides()
	{
		var (_, workerId, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetRatesAsync(workerId, workerId);

		result.UserCostRates.Should().ContainSingle();
		result.NodeRateOverrides.Should().ContainSingle();
	}

	[Fact]
	public async Task GetRatesAsync_returns_the_actors_current_roles()
	{
		var (administratorId, workerId, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetRatesAsync(administratorId, workerId);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetRatesAsync_returns_empty_for_an_employee_with_no_rate_data()
	{
		var (administratorId, _, _) = await SeedRatesAsync();
		var otherWorkerId = await SeedEmployeeAsync("Alan Turing", "alan.turing.rate-query", EmployeeRole.Worker);
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetRatesAsync(administratorId, otherWorkerId);

		result.UserCostRates.Should().BeEmpty();
		result.NodeRateOverrides.Should().BeEmpty();
	}

	[Fact]
	public async Task GetRatesAsync_throws_for_a_nonexistent_actor()
	{
		var (administratorId, workerId, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetRatesAsync(new(administratorId.Value + 999), workerId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetRatesAsync_throws_for_a_nonexistent_employee()
	{
		var (administratorId, _, _) = await SeedRatesAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetRatesAsync(administratorId, new(administratorId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IRateCommandPort CreateCommandPort(string connectionString);

	protected abstract IRateQueryPort CreateQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private async Task<(AppUserId AdministratorId, AppUserId WorkerId, JobNodeId RootJobNodeId)> SeedRatesAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace.rate-query",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.rate-query", EmployeeRole.Worker);

		var commandPort = CreateCommandPort(database.ConnectionString);
		_ = await commandPort.AddUserCostRateAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		_ = await commandPort.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(bootstrap.RootJobNodeId, new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		return (administratorId, workerId, bootstrap.RootJobNodeId);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string displayName, string userName, EmployeeRole role)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityUserCommand = connection.CreateCommand();
		identityUserCommand.CommandText = """
										  INSERT INTO identity_user
										  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										  VALUES
										  	(@appUserId, @userName, @normalizedUserName, 'test-hash', @securityStamp,
										  	 @concurrencyStamp, @requiresPasswordChange, @isEnabled, @lockoutEnabled, 0);
										  """;
		AddParameter(identityUserCommand, "@appUserId", appUserId.Value);
		AddParameter(identityUserCommand, "@userName", userName);
		AddParameter(identityUserCommand, "@normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@requiresPasswordChange", false);
		AddParameter(identityUserCommand, "@isEnabled", true);
		AddParameter(identityUserCommand, "@lockoutEnabled", true);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await AssignRoleAsync(connection, appUserId, role);

		return appUserId;
	}

	private static async Task AssignRoleAsync(DbConnection connection, AppUserId appUserId, EmployeeRole role)
	{
		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
