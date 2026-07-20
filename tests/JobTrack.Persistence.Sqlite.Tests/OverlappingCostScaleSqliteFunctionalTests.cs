namespace JobTrack.Persistence.Sqlite.Tests;

using System.Diagnostics;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Domain.Schedules;
using Microsoft.Data.Sqlite;
using NodaTime;
using TestSupport;

/// <summary>
///     §6.4/§8.4 of docs/plans/2026-07-09-overlapping-cost-scale-plan.md: SQLite carries no cost-
///     calculation latency budget (its documented single-writer envelope is exempt), but the
///     overlapping-cost scale's staircase shape must still compute cost through the real command ports
///     and <see cref="CostQueries" /> without unbounded blocking.
///     <see
///         cref="PerformanceScaleGenerator.SeedOverlappingCostScaleAsync" />
///     is PostgreSQL-only (server-side
///     <c>generate_series</c>/<c>unnest</c>/timestamptz arithmetic with no SQLite equivalent), so this
///     test builds the same staircase shape through the provider-agnostic command ports instead --
///     intentionally at a reduced worker count (3, not 50) since plan §2.4's own finding is that
///     sessions-per-worker, not worker count, dominates cost latency; each worker still gets the full
///     100-session, 6-deep staircase.
/// </summary>
public sealed class OverlappingCostScaleSqliteFunctionalTests : IAsyncLifetime
{
	private const int WorkerCount = 3;
	private const int LeavesPerWorker = 100;
	private const int OverlapDepth = 6;
	private static readonly TimeSpan Slot = TimeSpan.FromHours(1);
	private static readonly Instant BaseInstant = Instant.FromUtc(2026, 1, 1, 0, 0);
	private static readonly TimeSpan NoBlockingBound = TimeSpan.FromSeconds(30);

	private readonly SqliteDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Cost_calculation_over_the_staircase_shape_completes_without_unbounded_blocking()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), "1.2.3", "test-runner");
		await deployer.DeployAsync(scripts, CancellationToken.None);

		var bootstrapPort = new SqliteInstallationBootstrapPort(database.ConnectionString, SystemClock.Instance);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorContext = new CommandContext { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() };

		var jobNodePort = new SqliteJobNodeCommandPort(database.ConnectionString, SystemClock.Instance);
		var schedulePort = new SqliteScheduleCommandPort(database.ConnectionString, SystemClock.Instance);
		var ratePort = new SqliteRateCommandPort(database.ConnectionString, SystemClock.Instance);
		var sessionPort = new SqliteWorkSessionCommandPort(database.ConnectionString, SystemClock.Instance);

		var windowEnd = BaseInstant + Duration.FromTicks(Slot.Ticks * (LeavesPerWorker - 1 + OverlapDepth));
		var asOf = windowEnd + Duration.FromHours(1);

		long oneBranchId = default;
		foreach (var workerIndex in Enumerable.Range(1, WorkerCount)) {
			var workerId = await InsertWorkerAsync(connection, $"Overlap worker {workerIndex}");
			await GrantWorkerRoleAsync(connection, workerId);

			var branch = await jobNodePort.AddChildAsync(new() {
				Context = administratorContext,
				ParentId = bootstrap.RootJobNodeId,
				Description = $"Overlap worker {workerIndex} branch",
				OwnerUserId = workerId,
				Priority = Priority.Medium,
			});
			if (workerIndex == 1) {
				oneBranchId = branch.Id.Value;
			}

			_ = await schedulePort.AddScheduleExceptionAsync(new() {
				Context = administratorContext,
				UserId = workerId,
				Entry = new(ScheduleExceptionEffect.AddWorkingTime, new(BaseInstant, asOf), null),
				Reason = "Full working window for the overlapping-cost-scale SQLite smoke test",
			});
			_ = await ratePort.AddUserCostRateAsync(new() {
				Context = administratorContext,
				UserId = workerId,
				Rate = new(new(20m), BaseInstant, null),
			});

			for (var k = 1; k <= LeavesPerWorker; k++) {
				var leaf = await jobNodePort.AddChildAsync(new() {
					Context = administratorContext,
					ParentId = branch.Id,
					Description = $"Overlap worker {workerIndex} leaf {k}",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
				});
				_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = administratorContext, JobNodeId = leaf.Id });

				var session = await sessionPort.StartSessionAsync(new() {
					Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
					LeafWorkId = leaf.Id,
					WorkedByUserId = workerId,
				});
				_ = await sessionPort.CorrectSessionAsync(new() {
					Context = administratorContext,
					SessionId = session.Id,
					StartedAt = BaseInstant + Duration.FromTicks(Slot.Ticks * (k - 1)),
					FinishedAt = BaseInstant + Duration.FromTicks(Slot.Ticks * (k - 1 + OverlapDepth)),
					Reason = "Pin the staircase session to a deterministic instant",
					Version = session.Version,
				});
			}
		}

		var costQueries = new CostQueries(new SqliteCostQueryPort(database.ConnectionString, SystemClock.Instance));
		var stopwatch = Stopwatch.StartNew();
		var result = await costQueries.GetHierarchyTotalsAsync(new() { Context = administratorContext, NodeId = new(oneBranchId), AsOf = asOf });
		stopwatch.Stop();

		stopwatch.Elapsed.Should()
			.BeLessThan(NoBlockingBound, "no SQLite latency budget applies (§6.4), but the operation must not block indefinitely");
		result.ExactCosts.Should().NotBeEmpty();
		result.ExactCosts[new(oneBranchId)].Amount.Should().BePositive();
	}

	private static async Task PrepareConnectionAsync(SqliteConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<AppUserId> InsertWorkerAsync(SqliteConnection connection, string displayName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone)
							  VALUES ($displayName, 'Europe/London')
							  RETURNING id;
							  """;
		command.Parameters.AddWithValue("$displayName", displayName);
		return new((long)(await command.ExecuteScalarAsync())!);
	}

	private static async Task GrantWorkerRoleAsync(SqliteConnection connection, AppUserId appUserId)
	{
		await using var identityCommand = connection.CreateCommand();
		identityCommand.CommandText = """
									  INSERT INTO identity_user
									  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
									  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
									  VALUES
									  	($appUserId, $userName, $userName, 'test-hash', $securityStamp, $concurrencyStamp, 0, 1, 1, 0)
									  RETURNING id;
									  """;
		identityCommand.Parameters.AddWithValue("$appUserId", appUserId.Value);
		identityCommand.Parameters.AddWithValue("$userName", $"overlap-sqlite-worker-{appUserId.Value}".ToUpperInvariant());
		identityCommand.Parameters.AddWithValue("$securityStamp", Guid.NewGuid().ToString("N"));
		identityCommand.Parameters.AddWithValue("$concurrencyStamp", Guid.NewGuid().ToString("N"));
		var identityUserId = (long)(await identityCommand.ExecuteScalarAsync())!;

		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = "INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ($identityUserId, $roleId);";
		roleCommand.Parameters.AddWithValue("$identityUserId", identityUserId);
		roleCommand.Parameters.AddWithValue("$roleId", (short)EmployeeRole.Worker);
		_ = await roleCommand.ExecuteNonQueryAsync();
	}
}
