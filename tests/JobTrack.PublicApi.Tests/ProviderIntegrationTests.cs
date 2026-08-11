namespace JobTrack.PublicApi.Tests;

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Microsoft.Data.Sqlite;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using TestSupport;

public sealed class ProviderIntegrationTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "public-api-tests";

	[Fact]
	public async Task PostgreSql_bootstrap_times_out_without_retrying_and_succeeds_when_the_caller_retries_after_releasing_the_lock()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(
				SchemaProvider.PostgreSql,
				database.ConnectionString,
				static connection => Task.CompletedTask,
				static connection => new PostgreSqlSchemaVersionStore(),
				static connection => new PostgreSqlDeploymentLockStrategy());

			await using var lockConnection = new NpgsqlConnection(database.ConnectionString);
			await lockConnection.OpenAsync();
			await using (var lockCommand = lockConnection.CreateCommand()) {
				lockCommand.CommandText = "SELECT pg_advisory_lock(hashtext('jobtrack:bootstrap')::bigint);";
				_ = await lockCommand.ExecuteNonQueryAsync();
			}

			var timedOutConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { CommandTimeout = 1 }.ConnectionString;
			await using var timedOutDataSource = new NpgsqlDataSourceBuilder(timedOutConnectionString).UseNodaTime().Build();
			var timedOutClient = JobTrackPostgreSql.Create(timedOutDataSource);

			var act = () => timedOutClient.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.timeout"));

			var exception = (await act.Should().ThrowAsync<Exception>()).Which;
			exception.Should().BeAssignableTo<Exception>();
			(await CountRowsAsync(SchemaProvider.PostgreSql, database.ConnectionString, "app_user")).Should().Be(0);
			(await CountRowsAsync(SchemaProvider.PostgreSql, database.ConnectionString, "initialised_marker")).Should().Be(0);

			await using (var unlockCommand = lockConnection.CreateCommand()) {
				unlockCommand.CommandText = "SELECT pg_advisory_unlock(hashtext('jobtrack:bootstrap')::bigint);";
				_ = await unlockCommand.ExecuteNonQueryAsync();
			}

			await using var retryDataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
			var retryClient = JobTrackPostgreSql.Create(retryDataSource);

			var result = await retryClient.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.retry"));

			result.AdministratorId.Value.Should().BePositive();
			(await CountRowsAsync(SchemaProvider.PostgreSql, database.ConnectionString, "initialised_marker")).Should().Be(1);
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
			await DeployAsync(
				SchemaProvider.PostgreSql,
				database.ConnectionString,
				static connection => Task.CompletedTask,
				static connection => new PostgreSqlSchemaVersionStore(),
				static connection => new PostgreSqlDeploymentLockStrategy());

			await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
			var client = JobTrackPostgreSql.Create(dataSource);

			var bootstrapResult = await client.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.roles"));

			var accountState = await client.Query.GetAccountStateAsync(new() {
				Context = new() { Actor = bootstrapResult.AdministratorId, CorrelationId = Guid.NewGuid() },
				TargetUserId = bootstrapResult.AdministratorId,
			});

			accountState.Roles.Should().Contain(EmployeeRole.Administrator);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task PostgreSql_client_throws_object_disposed_after_the_shared_data_source_is_disposed()
	{
		await using var dataSource = NpgsqlDataSource.Create("Host=/tmp;Database=jobtrack-disposed-provider");
		var client = JobTrackPostgreSql.Create(dataSource);
		await dataSource.DisposeAsync();

		var act = () => client.Query.GetReadinessAsync(new() {
			Context = new() { Actor = new(1), CorrelationId = Guid.NewGuid() },
			NodeId = new(1),
		});

		await act.Should().ThrowAsync<ObjectDisposedException>();
	}

	[Fact]
	public async Task Sqlite_bootstrap_honours_a_pre_cancelled_token_without_writing_any_rows()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(
				SchemaProvider.Sqlite,
				database.ConnectionString,
				static async connection => {
					await using var command = connection.CreateCommand();
					command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
					_ = await command.ExecuteNonQueryAsync();
				},
				static connection => new SqliteSchemaVersionStore(),
				static connection => new SqliteDeploymentLockStrategy());

			var client = JobTrackSqlite.Create(database.ConnectionString);
			using var cancellation = new CancellationTokenSource();
			await cancellation.CancelAsync();

			var act = () => client.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.cancelled"), cancellation.Token);

			await act.Should().ThrowAsync<OperationCanceledException>();
			(await CountRowsAsync(SchemaProvider.Sqlite, database.ConnectionString, "app_user")).Should().Be(0);
			(await CountRowsAsync(SchemaProvider.Sqlite, database.ConnectionString, "initialised_marker")).Should().Be(0);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Sqlite_bootstrap_emits_a_bounded_activity_with_the_correlation_id_and_without_sensitive_tags()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(
				SchemaProvider.Sqlite,
				database.ConnectionString,
				static async connection => {
					await using var command = connection.CreateCommand();
					command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
					_ = await command.ExecuteNonQueryAsync();
				},
				static connection => new SqliteSchemaVersionStore(),
				static connection => new SqliteDeploymentLockStrategy());

			var stopped = new List<Activity>();
			using var listener = new ActivityListener {
				ShouldListenTo = source => source.Name == JobTrackDiagnostics.ActivitySourceName,
				Sample = static (ref _) => ActivitySamplingResult.AllData,
				ActivityStopped = stopped.Add,
			};
			ActivitySource.AddActivityListener(listener);
			var client = JobTrackSqlite.Create(database.ConnectionString);
			var request = CreateBootstrapRequest("ada.telemetry");

			_ = await client.Installation.BootstrapAdministratorAsync(request);

			var operation = stopped.Should()
				.ContainSingle(activity => activity.OperationName == "installation.bootstrap-administrator")
				.Which;
			operation.Status.Should().Be(ActivityStatusCode.Ok);
			operation.GetTagItem("jobtrack.correlation_id").Should().Be(request.CorrelationId.ToString("D"));
			operation.GetTagItem("jobtrack.user_name").Should().BeNull();
			operation.GetTagItem("jobtrack.password").Should().BeNull();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Sqlite_bootstrap_assigns_the_administrator_role_to_the_new_administrator()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(
				SchemaProvider.Sqlite,
				database.ConnectionString,
				static async connection => {
					await using var command = connection.CreateCommand();
					command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
					_ = await command.ExecuteNonQueryAsync();
				},
				static connection => new SqliteSchemaVersionStore(),
				static connection => new SqliteDeploymentLockStrategy());

			var client = JobTrackSqlite.Create(database.ConnectionString);

			var bootstrapResult = await client.Installation.BootstrapAdministratorAsync(CreateBootstrapRequest("ada.roles"));

			var accountState = await client.Query.GetAccountStateAsync(new() {
				Context = new() { Actor = bootstrapResult.AdministratorId, CorrelationId = Guid.NewGuid() },
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

	private static async Task DeployAsync(
		SchemaProvider provider,
		string connectionString,
		Func<DbConnection, Task> prepareConnectionAsync,
		Func<DbConnection, ISchemaVersionStore> createStore,
		Func<DbConnection, IDeploymentLockStrategy> createLockStrategy)
	{
		DbConnection connection = provider switch {
			SchemaProvider.PostgreSql => new NpgsqlConnection(connectionString),
			SchemaProvider.Sqlite => new SqliteConnection(connectionString),
			_ => throw new ArgumentOutOfRangeException(nameof(provider)),
		};
		await using var ownedConnection = connection;
		await connection.OpenAsync();
		await prepareConnectionAsync(connection);

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(provider));
		var deployer = new SchemaDeployer(connection, createStore(connection), createLockStrategy(connection), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private static async Task<int> CountRowsAsync(SchemaProvider provider, string connectionString, string tableName)
	{
		DbConnection connection = provider switch {
			SchemaProvider.PostgreSql => new NpgsqlConnection(connectionString),
			SchemaProvider.Sqlite => new SqliteConnection(connectionString),
			_ => throw new ArgumentOutOfRangeException(nameof(provider)),
		};
		await using var ownedConnection = connection;
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
		return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}
}
