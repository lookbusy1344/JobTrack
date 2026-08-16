namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Npgsql;
using TestSupport;

public sealed class BootstrapProviderIntegrationTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "public-api-tests";

	[Fact]
	public async Task PostgreSql_bootstrap_times_out_without_retrying_and_succeeds_when_the_caller_retries_after_releasing_the_lock()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(database.ConnectionString);

			await using var lockConnection = new NpgsqlConnection(database.ConnectionString);
			await lockConnection.OpenAsync();
			await using (var lockCommand = lockConnection.CreateCommand()) {
				lockCommand.CommandText = "SELECT pg_advisory_lock(hashtext('jobtrack:bootstrap')::bigint);";
				_ = await lockCommand.ExecuteNonQueryAsync();
			}

			var timedOutConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) {
				CommandTimeout = 1,
			}.ConnectionString;
			await using var timedOutDataSource = new NpgsqlDataSourceBuilder(timedOutConnectionString).UseNodaTime().Build();
			var timedOutClient = JobTrackPostgreSql.Create(timedOutDataSource);

			var act = () => timedOutClient.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.timeout"));

			var exception = (await act.Should().ThrowAsync<Exception>()).Which;
			exception.Should().BeAssignableTo<Exception>();
			(await CountRowsAsync(database.ConnectionString, "app_user")).Should().Be(0);
			(await CountRowsAsync(database.ConnectionString, "initialised_marker")).Should().Be(0);

			await using (var unlockCommand = lockConnection.CreateCommand()) {
				unlockCommand.CommandText = "SELECT pg_advisory_unlock(hashtext('jobtrack:bootstrap')::bigint);";
				_ = await unlockCommand.ExecuteNonQueryAsync();
			}

			await using var retryDataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
			var retryClient = JobTrackPostgreSql.Create(retryDataSource);

			var result = await retryClient.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.retry"));

			result.AdministratorId.Value.Should().BePositive();
			(await CountRowsAsync(database.ConnectionString, "initialised_marker")).Should().Be(1);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task PostgreSql_bootstrap_assigns_the_administrator_role_to_the_new_administrator()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(database.ConnectionString);

			await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
			var client = JobTrackPostgreSql.Create(dataSource);

			var bootstrapResult = await client.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.roles"));

			var accountState = await client.Query.GetAccountStateAsync(new() {
				Context = new() {
					Actor = bootstrapResult.AdministratorId,
					CorrelationId = Guid.NewGuid(),
				},
				TargetUserId = bootstrapResult.AdministratorId,
			});

			accountState.Roles.Should().Contain(EmployeeRole.Administrator);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static BootstrapAdministratorRequest CreateBootstrapRequest(string userName) => new() {
		DisplayName = "Ada Example",
		IanaTimeZone = "Europe/London",
		DefaultHourlyRate = new HourlyRate(25m),
		UserName = userName,
		Password = "correct-horse-battery-staple",
		CorrelationId = Guid.NewGuid(),
	};

	private static async Task DeployAsync(string connectionString)
	{
		DbConnection connection = new NpgsqlConnection(connectionString);
		await using var ownedConnection = connection;
		await connection.OpenAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private static async Task<int> CountRowsAsync(string connectionString, string tableName)
	{
		await using var connection = new NpgsqlConnection(connectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
		return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}
}
