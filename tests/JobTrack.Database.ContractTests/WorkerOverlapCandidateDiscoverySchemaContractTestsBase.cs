namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-COST-001 contract for schema slice 13b: worker-scoped,
///     database-wide overlap-candidate discovery (impl plan §6.2 item 13,
///     §6.5, spec §10.2.2, ADR 0017), asserted identically against PostgreSQL
///     and SQLite by <see cref="PostgreSqlWorkerOverlapCandidateDiscoverySchemaTests" />
///     and <see cref="SqliteWorkerOverlapCandidateDiscoverySchemaTests" />.
///     Hierarchy/achievement/readiness (13a) and the full cost-input sweep
///     (13c) are the sibling sub-slices of item 13.
/// </summary>
public abstract class WorkerOverlapCandidateDiscoverySchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;
	private const short AchievementWaiting = 1;

	private static readonly DateTimeOffset QueryStart = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset QueryEnd = new(2026, 1, 1, 17, 0, 0, TimeSpan.Zero);

	private readonly IDisposableTestDatabase database;

	private long? sharedRootId;

	protected WorkerOverlapCandidateDiscoverySchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	protected static DateTimeOffset AsOfInstant { get; } = new(2026, 1, 1, 18, 0, 0, TimeSpan.Zero);

	protected static DateTimeOffset QueryStartInstant => QueryStart;

	protected static DateTimeOffset QueryEndInstant => QueryEnd;

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_session_entirely_before_the_query_window_is_not_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafId, userId, QueryStart.AddHours(-3), QueryStart.AddHours(-1));

		(await OverlapCandidatesAsync(connection, userId)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_session_entirely_after_the_query_window_is_not_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafId, userId, QueryEnd.AddHours(1), QueryEnd.AddHours(2));

		(await OverlapCandidatesAsync(connection, userId)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_session_touching_the_query_start_boundary_is_not_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafId, userId, QueryStart.AddHours(-2), QueryStart);

		(await OverlapCandidatesAsync(connection, userId)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_session_touching_the_query_end_boundary_is_not_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafId, userId, QueryEnd, QueryEnd.AddHours(2));

		(await OverlapCandidatesAsync(connection, userId)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_session_overlapping_the_start_of_the_query_window_is_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafId, userId, QueryStart.AddHours(-1), QueryStart.AddHours(1));

		(await OverlapCandidatesAsync(connection, userId)).Should().ContainSingle(s => s.SessionId == sessionId);
	}

	[Fact]
	public async Task A_session_overlapping_the_end_of_the_query_window_is_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafId, userId, QueryEnd.AddHours(-1), QueryEnd.AddHours(1));

		(await OverlapCandidatesAsync(connection, userId)).Should().ContainSingle(s => s.SessionId == sessionId);
	}

	[Fact]
	public async Task A_session_fully_containing_the_query_window_is_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafId, userId, QueryStart.AddHours(-2), QueryEnd.AddHours(2));

		(await OverlapCandidatesAsync(connection, userId)).Should().ContainSingle(s => s.SessionId == sessionId);
	}

	[Fact]
	public async Task An_unfinished_session_started_before_the_query_window_ends_is_a_candidate_with_asof_as_its_effective_end()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafId, userId, QueryStart.AddHours(1), null);

		var candidates = await OverlapCandidatesAsync(connection, userId);

		candidates.Should().ContainSingle(s => s.SessionId == sessionId)
			.Which.EffectiveFinishedAt.Should().Be(AsOfInstant);
	}

	[Fact]
	public async Task An_unfinished_session_that_has_not_yet_started_by_the_query_end_is_not_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafId, userId, QueryEnd.AddHours(1), null);

		(await OverlapCandidatesAsync(connection, userId)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_session_belonging_to_a_different_user_is_not_a_candidate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		var (otherUserId, otherLeafId) = await SeedUserAndLeafAsync(connection, "Bob Example");
		await InsertSessionAsync(connection, otherLeafId, otherUserId, QueryStart.AddHours(1), QueryStart.AddHours(2));

		(await OverlapCandidatesAsync(connection, userId)).Should().BeEmpty();
	}

	[Fact]
	public async Task Overlapping_sessions_on_different_leaves_for_the_same_user_are_both_candidates()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafOneId) = await SeedUserAndLeafAsync(connection, "Alice Example");
		var leafTwoId = await InsertLeafNodeAsync(connection, userId, await GetRootIdAsync(connection, userId));
		var sessionOneId = await InsertSessionAsync(connection, leafOneId, userId, QueryStart.AddHours(1), QueryStart.AddHours(3));
		var sessionTwoId = await InsertSessionAsync(connection, leafTwoId, userId, QueryStart.AddHours(2), QueryStart.AddHours(4));

		var candidates = await OverlapCandidatesAsync(connection, userId);

		candidates.Select(s => s.SessionId).Should().BeEquivalentTo([sessionOneId, sessionTwoId]);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>PostgreSQL decodes a returned timestamptz/session-id column directly; SQLite decodes ADR 0007's epoch-ticks encoding.</summary>
	protected abstract DateTimeOffset DecodeInstant(object value);

	/// <summary>PostgreSQL invokes the <c>worker_overlapping_sessions</c> stored function; SQLite issues the equivalent raw parameterized query.</summary>
	protected abstract Task<IReadOnlyList<OverlapCandidate>> OverlapCandidatesAsync(DbConnection connection, long userId);

	private async Task<DbConnection> OpenDeployedConnectionAsync()
	{
		var connection = await OpenExistingConnectionAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private async Task<(long UserId, long LeafId)> SeedUserAndLeafAsync(DbConnection connection, string displayName)
	{
		var userId = await SeedAppUserAsync(connection, displayName);
		var rootId = await GetOrCreateRootAsync(connection, userId);
		var leafId = await InsertLeafNodeAsync(connection, userId, rootId);
		return (userId, leafId);
	}

	private async Task<long> GetOrCreateRootAsync(DbConnection connection, long creatingUserId)
	{
		sharedRootId ??= await InsertNodeAsync(connection, creatingUserId, null);
		return sharedRootId.Value;
	}

	private async Task<long> GetRootIdAsync(DbConnection connection, long userId) => await GetOrCreateRootAsync(connection, userId);

	private static async Task<long> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone)
							  VALUES (@displayName, 'Europe/London')
							  RETURNING id;
							  """;
		AddParameter(command, "@displayName", displayName);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> InsertLeafNodeAsync(DbConnection connection, long ownerUserId, long parentId)
	{
		var leafId = await InsertNodeAsync(connection, ownerUserId, parentId);
		await InsertLeafWorkAsync(connection, leafId);
		return leafId;
	}

	private async Task<long> InsertNodeAsync(DbConnection connection, long ownerUserId, long? parentId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @ownerUserId, @ownerUserId, @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", "A job");
		AddParameter(command, "@ownerUserId", ownerUserId);
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task InsertLeafWorkAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO leaf_work (job_node_id, achievement_id, changed_at)
							  VALUES (@jobNodeId, @achievementId, @changedAt);
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@achievementId", AchievementWaiting);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<long> InsertSessionAsync(
		DbConnection connection, long leafWorkId, long userId, DateTimeOffset startedAt, DateTimeOffset? finishedAt)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  VALUES (@leafWorkId, @userId, @startedAt, @finishedAt, @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@leafWorkId", leafWorkId);
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@startedAt", EncodeInstant(startedAt));
		AddParameter(command, "@finishedAt", finishedAt is null ? DBNull.Value : EncodeInstant(finishedAt.Value));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

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

/// <summary>One overlap-candidate session row (spec §10.2.2), scoped to one worker.</summary>
public readonly record struct OverlapCandidate(
	long SessionId,
	long LeafWorkId,
	DateTimeOffset StartedAt,
	DateTimeOffset? FinishedAt,
	DateTimeOffset EffectiveFinishedAt);
