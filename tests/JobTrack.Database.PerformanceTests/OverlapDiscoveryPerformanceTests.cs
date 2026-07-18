namespace JobTrack.Database.PerformanceTests;

using System.Diagnostics;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     §6.7 gate: worker-scoped, database-wide overlap discovery against the
///     "high concurrency" scale (docs/traceability/performance-budgets.md
///     §1/§2: one worker, 100 concurrent open work_session rows across 100
///     different leaves). PostgreSQL only.
/// </summary>
public sealed class OverlapDiscoveryPerformanceTests : IAsyncLifetime
{
	private static readonly TimeSpan OverlapDiscoveryBudget = TimeSpan.FromMilliseconds(75);

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Worker_overlapping_sessions_for_100_concurrent_sessions_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var instant = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
		var userId = await PerformanceScaleGenerator.SeedHighConcurrencyWorkerAsync(connection, instant);

		const string query = """
							 SELECT * FROM worker_overlapping_sessions(@userId, @queryStart, @queryEnd, @asOf)
							 """;
		(string Name, object Value)[] parameters = [
			("userId", userId),
			("queryStart", instant.AddHours(-2)),
			("queryEnd", instant),
			("asOf", instant),
		];

		var plan = await PostgreSqlExplainPlan.GetPlanAsync(connection, query, parameters);

		PostgreSqlExplainPlan.ContainsSequentialScanOf(plan, "work_session").Should().BeFalse(
			"worker-scoped overlap discovery must use the user-leading index, not a sequential scan of work_session");

		var stopwatch = Stopwatch.StartNew();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = query;
			foreach (var (name, value) in parameters) {
				var parameter = command.CreateParameter();
				parameter.ParameterName = name;
				parameter.Value = value;
				command.Parameters.Add(parameter);
			}

			await using var reader = await command.ExecuteReaderAsync();
			var rowCount = 0;
			while (await reader.ReadAsync()) {
				rowCount++;
			}

			rowCount.Should().Be(100);
		}

		stopwatch.Elapsed.Should().BeLessThan(OverlapDiscoveryBudget);
	}

	private async Task<NpgsqlConnection> OpenDeployedConnectionAsync()
	{
		var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), "1.2.3", "test-runner");
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}
}
