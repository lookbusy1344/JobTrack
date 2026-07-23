namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Abstractions;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlWorkSessionCommandPortTests()
	: WorkSessionCommandPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		CreateSessionPort(connectionString, SystemClock.Instance);

	protected override IWorkSessionCommandPort CreateSessionPort(string connectionString, IClock clock) =>
		new PostgreSqlWorkSessionCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), clock);

	protected override IAchievementCommandPort CreateAchievementPort(string connectionString) =>
		new PostgreSqlAchievementCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new PostgreSqlAuditQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	/// <summary>
	///     There is no advisory lock domain for work sessions (ADR 0012): schema version 0007's GiST
	///     exclusion constraint plus partial unique index is the sole mutual-exclusion mechanism, so this
	///     proves it holds under genuine PostgreSQL MVCC interleaving, not just single-threaded sequencing.
	/// </summary>
	[Fact]
	public async Task Concurrent_session_starts_for_the_same_worker_and_leaf_allow_exactly_one_to_succeed()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var portA = CreateSessionPort(ConnectionString);
		var portB = CreateSessionPort(ConnectionString);

		var results = await Task.WhenAll(
			TryStartSessionAsync(portA, workerId, leafId),
			TryStartSessionAsync(portB, workerId, leafId));

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(leafId)).ActiveSessionCount.Should().Be(1);
	}

	/// <summary>
	///     ADR 0048: starting a session on an unassigned node claims it via the same conditional
	///     <c>WHERE owner_user_id IS NULL</c> write <c>PickUpAsync</c> uses -- two different workers
	///     racing to start their own first session on the same unassigned leaf must have exactly one
	///     win the claim, the other seeing zero rows affected (<c>job-node-already-claimed</c>) rather
	///     than silently overwriting the winner's claim.
	/// </summary>
	[Fact]
	public async Task Concurrent_session_starts_by_different_workers_on_the_same_unassigned_leaf_allow_exactly_one_to_succeed()
	{
		var (rootId, jobManagerId, workerA, _) = await SeedReadyLeafAsync();
		var workerB = await SeedEmployeeAsync("Other Worker", "pg.unassigned-start-race.other", EmployeeRole.Worker);
		var jobNodePort = CreateJobNodePort(ConnectionString);
		var unassigned = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = unassigned.Id });

		var results = await Task.WhenAll(
			TryStartSessionAsync(CreateSessionPort(ConnectionString), workerA, unassigned.Id),
			TryStartSessionAsync(CreateSessionPort(ConnectionString), workerB, unassigned.Id));

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(unassigned.Id)).ActiveSessionCount.Should().Be(1);
	}

	/// <summary>
	///     ADR 0048: on an unassigned leaf, the loser of the race can surface either exception depending
	///     on interleaving -- the conditional claim losing after passing a stale "unassigned" read
	///     (<see cref="InvariantViolationException" />, "job-node-already-claimed"), or a fresh read
	///     already seeing the winner's committed claim, leaving <c>canRecordWork</c> to deny a
	///     non-controlling actor (<see cref="AuthorizationDeniedException" />) -- mirroring
	///     <c>PostgreSqlJobNodeCommandPortTests.TryPickUpAsync</c>'s identical dual-exception race.
	/// </summary>
	private static async Task<bool> TryStartSessionAsync(IWorkSessionCommandPort port, AppUserId workerId, JobNodeId leafId)
	{
		try {
			_ = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });
			return true;
		}
		catch (InvariantViolationException) {
			return false;
		}
		catch (AuthorizationDeniedException) {
			return false;
		}
	}

	/// <summary>
	///     ADR 0045 plan §6 race matrix: "reopen-and-start vs another reopen." Both racing calls read the
	///     same terminal leaf version; PostgreSQL's row-level optimistic-concurrency check on
	///     <c>leaf_work.row_version</c> (not a new advisory lock -- ADR 0045 adds none) must let exactly
	///     one through and reject the other with a stale-version conflict, never both.
	/// </summary>
	[Fact]
	public async Task Concurrent_reopen_and_start_attempts_on_the_same_terminal_leaf_allow_exactly_one_to_succeed()
	{
		var (_, jobManagerId, leafId) = await SeedTerminalLeafViaAchievementPortAsync();
		var portA = CreateSessionPort(ConnectionString);
		var portB = CreateSessionPort(ConnectionString);

		var results = await Task.WhenAll(
			TryReopenAndStartAsync(portA, jobManagerId, leafId),
			TryReopenAndStartAsync(portB, jobManagerId, leafId));

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(leafId)).Should().Be(new LeafState(Achievement.InProgress, false, 1));
	}

	/// <summary>ADR 0045 plan §6 race matrix: "reopen-and-start vs archive" -- the two are mutually exclusive outcomes.</summary>
	[Fact]
	public async Task Concurrent_reopen_and_start_vs_archive_leaves_a_consistent_final_state()
	{
		var (_, jobManagerId, leafId) = await SeedTerminalLeafViaAchievementPortAsync();
		var sessionPort = CreateSessionPort(ConnectionString);
		var jobNodePort = CreateJobNodePort(ConnectionString);

		var reopenTask = TryReopenAndStartAsync(sessionPort, jobManagerId, leafId);
		var archiveTask = TryArchiveAsync(jobNodePort, jobManagerId, leafId);
		var results = await Task.WhenAll(reopenTask, archiveTask);

		results.Count(succeeded => succeeded).Should().Be(1);
		var state = await ReadLeafStateAsync(leafId);
		state.Should().Be(results[0]
			? new LeafState(Achievement.InProgress, false, 1)
			: new LeafState(Achievement.Unsuccessful, true, 0));
	}

	/// <summary>
	///     ADR 0045 plan §6 race matrix: "complete vs a new session start." Completing finishes the
	///     confirmed session set and closes the leaf; a concurrent new session start must not land on
	///     top of that -- either it commits first (making <c>CompleteLeafAsync</c>'s expected active-set
	///     stale) or the leaf closes first (rejecting the new start as <c>work-session-leaf-closed</c>).
	///     PostgreSQL's row-level optimistic-concurrency check on <c>leaf_work.row_version</c> and
	///     <c>work_session.row_version</c> (no advisory lock here -- ADR 0045 adds none) lets exactly one
	///     of the two attempts through under genuine MVCC interleaving.
	/// </summary>
	[Fact]
	public async Task Concurrent_complete_vs_a_new_session_start_leaves_a_consistent_final_state()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "pg.complete-vs-start.other", EmployeeRole.Worker);
		var sessionPort = CreateSessionPort(ConnectionString);
		var session = await sessionPort.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });

		var completeTask = TryCompleteLeafAsync(CreateSessionPort(ConnectionString), jobManagerId, leafId, session.Id, session.Version);
		var startTask = TryStartSessionForAsync(CreateSessionPort(ConnectionString), jobManagerId, otherWorkerId, leafId);
		var results = await Task.WhenAll(completeTask, startTask);

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(leafId)).Should().Be(results[0]
			? new LeafState(Achievement.Success, false, 0)
			: new LeafState(Achievement.InProgress, false, 2));
	}

	/// <summary>
	///     ADR 0045 plan §6 race matrix: "complete vs another session finish." Completing finishes every
	///     session in its confirmed set at one instant; a concurrent independent finish of one of those
	///     same sessions must not double-apply -- either it commits first (making the completion's
	///     expected session version stale) or the completion finishes it first (making the independent
	///     finish's expected version stale). Exactly one of the two succeeds.
	/// </summary>
	[Fact]
	public async Task Concurrent_complete_vs_another_session_finish_leaves_a_consistent_final_state()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var sessionPort = CreateSessionPort(ConnectionString);
		var session = await sessionPort.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });

		var completeTask = TryCompleteLeafAsync(CreateSessionPort(ConnectionString), jobManagerId, leafId, session.Id, session.Version);
		var finishTask = TryFinishSessionAsync(CreateSessionPort(ConnectionString), workerId, session.Id, session.Version);
		var results = await Task.WhenAll(completeTask, finishTask);

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(leafId)).Should().Be(
			new LeafState(results[0] ? Achievement.Success : Achievement.InProgress, false, 0));
	}

	/// <summary>
	///     ADR 0045 plan §6 race matrix: "complete vs correction." Completing finishes a session as part
	///     of its confirmed set; a concurrent historical correction of that same session must not land
	///     on a version the completion already advanced, and vice versa. Exactly one of the two succeeds.
	/// </summary>
	[Fact]
	public async Task Concurrent_complete_vs_correction_of_the_same_session_leaves_a_consistent_final_state()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var sessionPort = CreateSessionPort(ConnectionString);
		var session = await sessionPort.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });

		var completeTask = TryCompleteLeafAsync(CreateSessionPort(ConnectionString), jobManagerId, leafId, session.Id, session.Version);
		var correctTask = TryCorrectSessionAsync(CreateSessionPort(ConnectionString), jobManagerId, session.Id, session.StartedAt, session.Version);
		var results = await Task.WhenAll(completeTask, correctTask);

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(leafId)).Should().Be(results[0]
			? new LeafState(Achievement.Success, false, 0)
			: new LeafState(Achievement.InProgress, false, 1));
	}

	/// <summary>
	///     ADR 0045 plan §6 race matrix: reopening a successful prerequisite and starting its dependent
	///     must serialize around the dependent's readiness decision. Whichever commits first invalidates
	///     the other's premise; both must never commit from the same formerly-ready snapshot.
	/// </summary>
	[Fact]
	public async Task Concurrent_reopen_of_a_former_prerequisite_vs_dependent_start_leaves_a_consistent_final_state()
	{
		var (rootId, jobManagerId, workerId, requiredLeafId) = await SeedReadyLeafAsync();
		var sessionPort = CreateSessionPort(ConnectionString);
		var requiredSession = await sessionPort.StartWorkAsync(
			new() { Context = ContextFor(workerId), JobNodeId = requiredLeafId, WorkedByUserId = workerId });
		_ = await sessionPort.FinishSessionAsync(
			new() { Context = ContextFor(workerId), SessionId = requiredSession.Id, Version = requiredSession.Version });
		_ = await CreateAchievementPort(ConnectionString).SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = requiredLeafId,
			NewAchievement = Achievement.Success,
			Reason = "Ready for dependent work",
			Version = 2,
		});
		var jobNodePort = CreateJobNodePort(ConnectionString);
		var dependent = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Dependent work",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = dependent.Id });
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = requiredLeafId,
			DependentJobId = dependent.Id,
		});

		var results = await Task.WhenAll(
			TryReopenAndStartAsync(CreateSessionPort(ConnectionString), jobManagerId, requiredLeafId),
			TryStartSessionForAsync(CreateSessionPort(ConnectionString), jobManagerId, workerId, dependent.Id));

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(requiredLeafId)).Should().Be(results[0]
			? new LeafState(Achievement.InProgress, false, 1)
			: new LeafState(Achievement.Success, false, 0));
		(await ReadLeafStateAsync(dependent.Id)).Should().Be(results[1]
			? new LeafState(Achievement.Waiting, false, 1)
			: new LeafState(Achievement.Waiting, false, 0));
	}

	private static async Task<bool> TryCompleteLeafAsync(
		IWorkSessionCommandPort port, AppUserId actorId, JobNodeId leafId, WorkSessionId sessionId, long sessionVersion)
	{
		try {
			_ = await port.CompleteLeafAsync(new() {
				Context = ContextFor(actorId),
				JobNodeId = leafId,
				Version = 2,
				ExpectedActiveSessions = [new() { Id = sessionId, Version = sessionVersion }],
			});
			return true;
		}
		catch (Exception ex) when (ex is InvariantViolationException or ConcurrencyConflictException) {
			return false;
		}
	}

	private static async Task<bool> TryStartSessionForAsync(
		IWorkSessionCommandPort port, AppUserId actorId, AppUserId targetWorkerId, JobNodeId leafId)
	{
		try {
			_ = await port.StartSessionAsync(new() { Context = ContextFor(actorId), LeafWorkId = leafId, WorkedByUserId = targetWorkerId });
			return true;
		}
		catch (Exception ex) when (ex is InvariantViolationException or ConcurrencyConflictException or PrerequisiteBlockedException) {
			return false;
		}
	}

	private static async Task<bool> TryFinishSessionAsync(IWorkSessionCommandPort port, AppUserId actorId, WorkSessionId sessionId, long version)
	{
		try {
			_ = await port.FinishSessionAsync(new() { Context = ContextFor(actorId), SessionId = sessionId, Version = version });
			return true;
		}
		catch (Exception ex) when (ex is InvariantViolationException or ConcurrencyConflictException) {
			return false;
		}
	}

	private static async Task<bool> TryCorrectSessionAsync(
		IWorkSessionCommandPort port, AppUserId actorId, WorkSessionId sessionId, Instant startedAt, long version)
	{
		try {
			_ = await port.CorrectSessionAsync(new() {
				Context = ContextFor(actorId),
				SessionId = sessionId,
				StartedAt = startedAt,
				Reason = "Racing correction",
				Version = version,
			});
			return true;
		}
		catch (Exception ex) when (ex is InvariantViolationException or ConcurrencyConflictException) {
			return false;
		}
	}

	private async Task<(JobNodeId RootId, AppUserId JobManagerId, JobNodeId LeafId)> SeedTerminalLeafViaAchievementPortAsync()
	{
		var (rootId, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var sessionPort = CreateSessionPort(ConnectionString);
		var session = await sessionPort.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });
		_ = await sessionPort.FinishSessionAsync(new() { Context = ContextFor(workerId), SessionId = session.Id, Version = session.Version });
		var achievementPort = CreateAchievementPort(ConnectionString);
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not work out",
			Version = 2,
		});

		return (rootId, jobManagerId, leafId);
	}

	private static async Task<bool> TryReopenAndStartAsync(IWorkSessionCommandPort port, AppUserId actorId, JobNodeId leafId)
	{
		try {
			_ = await port.ReopenAndStartWorkAsync(new() {
				Context = ContextFor(actorId),
				JobNodeId = leafId,
				Version = 3,
				Reason = "Racing reopen",
				WorkedByUserId = actorId,
			});
			return true;
		}
		catch (Exception ex) when (ex is InvariantViolationException or ConcurrencyConflictException) {
			return false;
		}
	}

	private static async Task<bool> TryArchiveAsync(IJobNodeCommandPort port, AppUserId actorId, JobNodeId leafId)
	{
		try {
			_ = await port.ArchiveAsync(new() { Context = ContextFor(actorId), NodeId = leafId, Version = 1 });
			return true;
		}
		catch (Exception ex) when (ex is InvariantViolationException or ConcurrencyConflictException) {
			return false;
		}
	}

	private async Task<LeafState> ReadLeafStateAsync(JobNodeId leafId)
	{
		await using var connection = CreateConnection(ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT a.name,
							         jn.archived_at IS NOT NULL,
							         (SELECT COUNT(*) FROM work_session ws WHERE ws.leaf_work_id = lw.job_node_id AND ws.finished_at IS NULL)
							  FROM leaf_work lw
							  JOIN achievement_status a ON a.id = lw.achievement_id
							  JOIN job_node jn ON jn.id = lw.job_node_id
							  WHERE lw.job_node_id = @leafId;
							  """;
		var parameter = command.CreateParameter();
		parameter.ParameterName = "@leafId";
		parameter.Value = leafId.Value;
		_ = command.Parameters.Add(parameter);
		await using var reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue();

		return new(Enum.Parse<Achievement>(reader.GetString(0)), reader.GetBoolean(1), checked((int)reader.GetInt64(2)));
	}

	private readonly record struct LeafState(Achievement Achievement, bool IsArchived, int ActiveSessionCount);
}
