namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-SESSION-001 contract for schema slice 7: <c>work_session</c>,
///     interval ordering, active-session uniqueness, and same-user/same-leaf
///     non-overlap (impl plan §6.2 item 7, spec §4.4), asserted identically
///     against PostgreSQL and SQLite by <see cref="PostgreSqlWorkSessionSchemaTests" />
///     and <see cref="SqliteWorkSessionSchemaTests" />.
/// </summary>
public abstract class WorkSessionSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	// achievement_status ids, schema version 0001: Waiting=1, InProgress=2,
	// Success=3, Cancelled=4, Unsuccessful=5 -- 3/4/5 are ADR 0044's terminal set.
	private const int WaitingAchievementId = 1;
	private const int TerminalSuccessAchievementId = 3;
	private const int TerminalCancelledAchievementId = 4;
	private const int TerminalUnsuccessfulAchievementId = 5;

	private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

	private readonly IDisposableTestDatabase database;

	protected WorkSessionSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_work_session_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "work_session")).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_a_finished_session_with_finish_after_start_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");

		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		id.Should().BePositive();
		await AssertSessionRangeAsync(connection, id, Epoch, Epoch.AddHours(1));
	}

	[Fact]
	public async Task Inserting_an_unfinished_session_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");

		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		id.Should().BePositive();
		await AssertSessionRangeAsync(connection, id, Epoch, null);
	}

	[Fact]
	public async Task Inserting_a_session_finishing_at_or_before_it_started_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Overlapping_sessions_for_the_same_user_and_leaf_work_are_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(2));

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(1), Epoch.AddHours(3));

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_second_active_session_for_the_same_user_and_leaf_work_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(5), null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Sessions_touching_at_a_boundary_do_not_overlap()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(1), Epoch.AddHours(2));

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_sessions_for_the_same_user_on_different_leaves_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, firstLeafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var secondLeafWorkId = await AddLeafWorkUnderRootAsync(connection, userId);
		await InsertSessionAsync(connection, firstLeafWorkId, userId, Epoch, Epoch.AddHours(2));

		var id = await InsertSessionAsync(connection, secondLeafWorkId, userId, Epoch, Epoch.AddHours(2));

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_sessions_for_different_users_on_the_same_leaf_work_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (firstUserId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var (secondUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		await InsertSessionAsync(connection, leafWorkId, firstUserId, Epoch, Epoch.AddHours(2));

		var id = await InsertSessionAsync(connection, leafWorkId, secondUserId, Epoch, Epoch.AddHours(2));

		id.Should().BePositive();
	}

	[Fact]
	public async Task Concurrent_overlapping_sessions_for_the_same_user_and_leaf_work_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertSessionAsync(connectionA, leafWorkId, userId, Epoch, Epoch.AddHours(2)),
			TryInsertSessionAsync(connectionB, leafWorkId, userId, Epoch.AddHours(1), Epoch.AddHours(3)));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	// ADR 0044: closed-leaf session creation and closure-vs-active-session
	// serialization.

	[Theory]
	[InlineData(TerminalSuccessAchievementId)]
	[InlineData(TerminalCancelledAchievementId)]
	[InlineData(TerminalUnsuccessfulAchievementId)]
	public async Task Inserting_an_active_session_for_a_leaf_with_terminal_achievement_is_rejected(int terminalAchievementId)
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await SetAchievementAsync(connection, leafWorkId, terminalAchievementId);

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_backdated_active_session_for_a_leaf_with_terminal_achievement_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(-100), null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_an_active_session_for_an_archived_leaf_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await ArchiveNodeAsync(connection, leafWorkId, true);

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_finished_session_for_an_archived_leaf_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await ArchiveNodeAsync(connection, leafWorkId, true);

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_finished_session_for_a_leaf_with_terminal_achievement_succeeds()
	{
		// Subtree import inserts already-finished historical sessions and sets the leaf's terminal
		// achievement inside one transaction (ADR 0044); only an archived leaf rejects a new row
		// outright regardless of whether it is already finished.
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);

		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		id.Should().BePositive();
	}

	[Fact]
	public async Task Updating_a_finished_session_back_to_active_on_a_terminal_leaf_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));
		await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);

		var act = async () => await SetSessionFinishedAtAsync(connection, sessionId, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Updating_a_finished_session_back_to_active_on_an_archived_leaf_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));
		await ArchiveNodeAsync(connection, leafWorkId, true);

		var act = async () => await SetSessionFinishedAtAsync(connection, sessionId, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Correcting_a_finished_session_remains_valid_on_a_terminal_leaf()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));
		await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);

		await SetSessionFinishedAtAsync(connection, sessionId, Epoch.AddHours(2));

		await AssertSessionRangeAsync(connection, sessionId, Epoch, Epoch.AddHours(2));
	}

	[Fact]
	public async Task Correcting_a_finished_session_remains_valid_on_an_archived_leaf()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));
		await ArchiveNodeAsync(connection, leafWorkId, true);

		await SetSessionFinishedAtAsync(connection, sessionId, Epoch.AddHours(2));

		await AssertSessionRangeAsync(connection, sessionId, Epoch, Epoch.AddHours(2));
	}

	[Theory]
	[InlineData(TerminalSuccessAchievementId)]
	[InlineData(TerminalCancelledAchievementId)]
	[InlineData(TerminalUnsuccessfulAchievementId)]
	public async Task Transitioning_achievement_to_a_terminal_state_while_a_session_is_active_is_rejected(int terminalAchievementId)
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		var act = async () => await SetAchievementAsync(connection, leafWorkId, terminalAchievementId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Transitioning_achievement_to_a_terminal_state_with_no_active_session_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var sessionId = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);

		(await ReadAchievementIdAsync(connection, leafWorkId)).Should().Be(TerminalSuccessAchievementId);
		sessionId.Should().BePositive();
	}

	[Fact]
	public async Task A_leaf_with_multiple_active_workers_blocks_terminal_achievement_transition()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (firstUserId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var (secondUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		await InsertSessionAsync(connection, leafWorkId, firstUserId, Epoch, Epoch.AddHours(1));
		await InsertSessionAsync(connection, leafWorkId, secondUserId, Epoch, null);

		var act = async () => await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Archiving_a_leaf_with_an_active_session_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		var act = async () => await ArchiveNodeAsync(connection, leafWorkId, true);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Archiving_a_leaf_with_no_active_session_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		await ArchiveNodeAsync(connection, leafWorkId, true);

		(await IsArchivedAsync(connection, leafWorkId)).Should().BeTrue();
	}

	[Fact]
	public async Task Reopening_achievement_alone_leaves_an_archived_leaf_closed_to_new_sessions()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));
		await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);
		await ArchiveNodeAsync(connection, leafWorkId, true);

		await SetAchievementAsync(connection, leafWorkId, WaitingAchievementId);
		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(2), null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Reopening_achievement_and_restoring_an_archived_leaf_together_allow_a_new_session()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));
		await SetAchievementAsync(connection, leafWorkId, TerminalSuccessAchievementId);
		await ArchiveNodeAsync(connection, leafWorkId, true);

		await SetAchievementAsync(connection, leafWorkId, WaitingAchievementId);
		await ArchiveNodeAsync(connection, leafWorkId, false);
		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(2), null);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Concurrent_session_start_and_terminal_achievement_transition_serialize_to_one_valid_committed_state()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var outcomes = await Task.WhenAll(
			TryInsertSessionAsync(connectionA, leafWorkId, userId, Epoch, null),
			TrySetAchievementAsync(connectionB, leafWorkId, TerminalSuccessAchievementId));

		var hasActiveSession = await HasActiveSessionAsync(seedConnection, leafWorkId);
		var achievementId = await ReadAchievementIdAsync(seedConnection, leafWorkId);

		(achievementId is TerminalSuccessAchievementId && hasActiveSession).Should().BeFalse(
			"a terminal leaf must never carry an active session, whichever operation won the race");
		outcomes.Should().ContainSingle(succeeded => succeeded, "exactly one incompatible operation must commit");
	}

	[Fact]
	public async Task Concurrent_session_start_and_leaf_archive_serialize_to_one_valid_committed_state()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var outcomes = await Task.WhenAll(
			TryInsertSessionAsync(connectionA, leafWorkId, userId, Epoch, null),
			TryArchiveNodeAsync(connectionB, leafWorkId));

		var hasActiveSession = await HasActiveSessionAsync(seedConnection, leafWorkId);
		var archived = await IsArchivedAsync(seedConnection, leafWorkId);

		(archived && hasActiveSession).Should().BeFalse(
			"an archived leaf must never carry an active session, whichever operation won the race");
		outcomes.Should().ContainSingle(succeeded => succeeded, "exactly one incompatible operation must commit");
	}

	[Fact]
	public async Task Concurrent_session_reactivation_and_terminal_achievement_transition_serialize_to_one_valid_committed_state()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");
		var sessionId = await InsertSessionAsync(seedConnection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();
		var outcomes = await Task.WhenAll(
			TrySetSessionFinishedAtAsync(connectionA, sessionId, null),
			TrySetAchievementAsync(connectionB, leafWorkId, TerminalSuccessAchievementId));

		var hasActiveSession = await HasActiveSessionAsync(seedConnection, leafWorkId);
		var achievementId = await ReadAchievementIdAsync(seedConnection, leafWorkId);

		(achievementId is TerminalSuccessAchievementId && hasActiveSession).Should().BeFalse();
		outcomes.Should().ContainSingle(succeeded => succeeded, "reactivation and terminal closure are incompatible");
	}

	[Fact]
	public async Task Concurrent_session_reactivation_and_leaf_archive_serialize_to_one_valid_committed_state()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");
		var sessionId = await InsertSessionAsync(seedConnection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();
		var outcomes = await Task.WhenAll(
			TrySetSessionFinishedAtAsync(connectionA, sessionId, null),
			TryArchiveNodeAsync(connectionB, leafWorkId));

		var hasActiveSession = await HasActiveSessionAsync(seedConnection, leafWorkId);
		var archived = await IsArchivedAsync(seedConnection, leafWorkId);

		(archived && hasActiveSession).Should().BeFalse();
		outcomes.Should().ContainSingle(succeeded => succeeded, "reactivation and archive closure are incompatible");
	}

	[Fact]
	public async Task Concurrent_last_session_finish_and_terminal_achievement_transition_never_strand_the_session()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");
		var sessionId = await InsertSessionAsync(seedConnection, leafWorkId, userId, Epoch, null);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();
		var outcomes = await Task.WhenAll(
			TrySetSessionFinishedAtAsync(connectionA, sessionId, Epoch.AddHours(1)),
			TrySetAchievementAsync(connectionB, leafWorkId, TerminalSuccessAchievementId));

		(await HasActiveSessionAsync(seedConnection, leafWorkId)).Should().BeFalse("finish must remain available during closure");
		outcomes[0].Should().BeTrue("the finish side of the race must always commit");
		(await ReadAchievementIdAsync(seedConnection, leafWorkId) is TerminalSuccessAchievementId).Should().Be(outcomes[1]);
	}

	[Fact]
	public async Task Concurrent_last_session_finish_and_leaf_archive_never_strand_the_session()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");
		var sessionId = await InsertSessionAsync(seedConnection, leafWorkId, userId, Epoch, null);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();
		var outcomes = await Task.WhenAll(
			TrySetSessionFinishedAtAsync(connectionA, sessionId, Epoch.AddHours(1)),
			TryArchiveNodeAsync(connectionB, leafWorkId));

		(await HasActiveSessionAsync(seedConnection, leafWorkId)).Should().BeFalse("finish must remain available during closure");
		outcomes[0].Should().BeTrue("the finish side of the race must always commit");
		(await IsArchivedAsync(seedConnection, leafWorkId)).Should().Be(outcomes[1]);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>
	///     Drift check for the generated <c>work_session.session_range</c> column (remediation plan
	///     §3.1): a no-op on providers with no such column. PostgreSQL overrides this to read the
	///     stored range back and assert it matches <paramref name="startedAt" />/<paramref name="finishedAt" />.
	/// </summary>
	protected virtual Task AssertSessionRangeAsync(DbConnection connection, long sessionId, DateTimeOffset startedAt, DateTimeOffset? finishedAt) =>
		Task.CompletedTask;

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

	private async Task<(long UserId, long LeafWorkId)> SeedUserAndLeafWorkAsync(DbConnection connection, string displayName)
	{
		var (userId, _) = await SeedAppUserAsync(connection, displayName);
		var leafWorkId = await AddLeafWorkUnderRootAsync(connection, userId);
		return (userId, leafWorkId);
	}

	private async Task<long> AddLeafWorkUnderRootAsync(DbConnection connection, long ownerUserId)
	{
		var rootId = await FindOrCreateRootAsync(connection, ownerUserId);
		var leafId = await InsertNodeAsync(connection, ownerUserId, rootId);
		await InsertLeafWorkAsync(connection, leafId);
		return leafId;
	}

	private static async Task<long?> TryFindRootAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT id FROM job_node WHERE parent_id IS NULL;";
		var result = await command.ExecuteScalarAsync();
		return result is null ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
	}

	private async Task<long> FindOrCreateRootAsync(DbConnection connection, long ownerUserId)
	{
		var existingRootId = await TryFindRootAsync(connection);
		return existingRootId ?? await InsertNodeAsync(connection, ownerUserId, null);
	}

	private static async Task<(long AppUserId, long IdentityUserId)> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		return (appUserId, 0);
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
							  INSERT INTO leaf_work (job_node_id, changed_at)
							  VALUES (@jobNodeId, @changedAt);
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<long> InsertSessionAsync(
		DbConnection connection, long leafWorkId, long workedByUserId, DateTimeOffset startedAt, DateTimeOffset? finishedAt)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  VALUES (@leafWorkId, @workedByUserId, @startedAt, @finishedAt, @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@leafWorkId", leafWorkId);
		AddParameter(command, "@workedByUserId", workedByUserId);
		AddParameter(command, "@startedAt", EncodeInstant(startedAt));
		AddParameter(command, "@finishedAt", finishedAt is null ? DBNull.Value : EncodeInstant(finishedAt.Value));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<bool> TryInsertSessionAsync(
		DbConnection connection, long leafWorkId, long workedByUserId, DateTimeOffset startedAt, DateTimeOffset? finishedAt)
	{
		try {
			await InsertSessionAsync(connection, leafWorkId, workedByUserId, startedAt, finishedAt);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task SetAchievementAsync(DbConnection connection, long leafWorkId, int achievementId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  UPDATE leaf_work SET achievement_id = @achievementId, changed_at = @changedAt, row_version = row_version + 1
							  WHERE job_node_id = @leafWorkId;
							  """;
		AddParameter(command, "@achievementId", achievementId);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));
		AddParameter(command, "@leafWorkId", leafWorkId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> TrySetAchievementAsync(DbConnection connection, long leafWorkId, int achievementId)
	{
		try {
			await SetAchievementAsync(connection, leafWorkId, achievementId);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task ArchiveNodeAsync(DbConnection connection, long jobNodeId, bool archived)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE job_node SET archived_at = @archivedAt, row_version = row_version + 1 WHERE id = @jobNodeId;";
		AddParameter(command, "@archivedAt", archived ? EncodeInstant(DateTimeOffset.UtcNow) : DBNull.Value);
		AddParameter(command, "@jobNodeId", jobNodeId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> TryArchiveNodeAsync(DbConnection connection, long jobNodeId)
	{
		try {
			await ArchiveNodeAsync(connection, jobNodeId, true);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task SetSessionFinishedAtAsync(DbConnection connection, long sessionId, DateTimeOffset? finishedAt)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  UPDATE work_session SET finished_at = @finishedAt, changed_at = @changedAt, row_version = row_version + 1
							  WHERE id = @id;
							  """;
		AddParameter(command, "@finishedAt", finishedAt is null ? DBNull.Value : EncodeInstant(finishedAt.Value));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));
		AddParameter(command, "@id", sessionId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> TrySetSessionFinishedAtAsync(DbConnection connection, long sessionId, DateTimeOffset? finishedAt)
	{
		try {
			await SetSessionFinishedAtAsync(connection, sessionId, finishedAt);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private static async Task<int> ReadAchievementIdAsync(DbConnection connection, long leafWorkId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT achievement_id FROM leaf_work WHERE job_node_id = @leafWorkId;";
		AddParameter(command, "@leafWorkId", leafWorkId);
		return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<bool> IsArchivedAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT archived_at FROM job_node WHERE id = @jobNodeId;";
		AddParameter(command, "@jobNodeId", jobNodeId);
		var result = await command.ExecuteScalarAsync();
		return result is not (null or DBNull);
	}

	private static async Task<bool> HasActiveSessionAsync(DbConnection connection, long leafWorkId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM work_session WHERE leaf_work_id = @leafWorkId AND finished_at IS NULL;";
		AddParameter(command, "@leafWorkId", leafWorkId);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
	}

	private static async Task<long> CountRowsAsync(DbConnection connection, string tableName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
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
