namespace JobTrack.Persistence.Sqlite.Tests;

using System.Data.Common;
using Abstractions;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using TestSupport;

public sealed class SqliteWorkSessionCommandPortTests()
	: WorkSessionCommandPortContractTestsBase(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;

	protected override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new SqliteSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new SqliteDeploymentLockStrategy();

	protected override async Task PrepareConnectionAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
		_ = await command.ExecuteNonQueryAsync();
	}

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new SqliteInstallationBootstrapPort(connectionString, SystemClock.Instance);

	internal override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new SqliteJobNodeCommandPort(connectionString, SystemClock.Instance);

	internal override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		CreateSessionPort(connectionString, SystemClock.Instance);

	internal override IWorkSessionCommandPort CreateSessionPort(string connectionString, IClock clock) =>
		new SqliteWorkSessionCommandPort(connectionString, clock);

	internal override IAchievementCommandPort CreateAchievementPort(string connectionString) =>
		new SqliteAchievementCommandPort(connectionString, SystemClock.Instance);

	internal override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new SqliteAuditQueryPort(connectionString, SystemClock.Instance);

	[Fact]
	public Task Concurrent_compound_finish_with_write_up_vs_node_edit_has_exactly_one_complete_outcome() =>
		AssertConcurrentFinishWithWriteUpVersusNodeEditAsync();

	[Fact]
	public Task Concurrent_compound_finish_with_write_up_vs_session_finish_has_exactly_one_complete_outcome() =>
		AssertConcurrentFinishWithWriteUpVersusSessionFinishAsync();

	/// <summary>
	///     ADR 0045 plan §6 race matrix: "reopen-and-start vs another reopen." SQLite has no advisory
	///     lock; <c>BEGIN IMMEDIATE</c> serializes the two attempts through SQLite's single-writer model
	///     (matches <see cref="SqliteJobNodeCommandPort" />'s established technique), so exactly one must
	///     succeed and the other must see a stale version, never both.
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

	/// <summary>
	///     ADR 0048: starting a session on an unassigned node claims it via the same conditional
	///     <c>WHERE owner_user_id IS NULL</c> write <c>PickUpAsync</c> uses. SQLite's
	///     <c>
	///         BEGIN
	///         IMMEDIATE
	///     </c>
	///     serializes the two attempts through its single-writer model, so exactly one
	///     worker must win the claim and the other must see <c>job-node-already-claimed</c>, never both.
	/// </summary>
	[Fact]
	public async Task Concurrent_session_starts_by_different_workers_on_the_same_unassigned_leaf_allow_exactly_one_to_succeed()
	{
		var (rootId, jobManagerId, workerA, _) = await SeedReadyLeafAsync();
		var workerB = await SeedEmployeeAsync("Other Worker", "sqlite.unassigned-start-race.other", EmployeeRole.Worker);
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
			TryStartSessionForAsync(CreateSessionPort(ConnectionString), workerA, workerA, unassigned.Id),
			TryStartSessionForAsync(CreateSessionPort(ConnectionString), workerB, workerB, unassigned.Id));

		results.Count(succeeded => succeeded).Should().Be(1);
		(await ReadLeafStateAsync(unassigned.Id)).ActiveSessionCount.Should().Be(1);
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
	///     SQLite's single-writer <c>BEGIN IMMEDIATE</c> serializes the two attempts, so exactly one
	///     succeeds, never both.
	/// </summary>
	[Fact]
	public async Task Concurrent_complete_vs_a_new_session_start_leaves_a_consistent_final_state()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "sqlite.complete-vs-start.other", EmployeeRole.Worker);
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

	/// <summary>
	///     ADR 0048: on an unassigned leaf raced by a non-controlling actor, SQLite's
	///     <c>
	///         BEGIN
	///         IMMEDIATE
	///     </c>
	///     serialization means the loser's own auto-claim read can already see the
	///     winner's committed ownership, leaving <c>canRecordWork</c> to deny it
	///     (<see cref="AuthorizationDeniedException" />) rather than the claim's own conditional write
	///     losing (<see cref="InvariantViolationException" />) -- both mean "did not win the race."
	/// </summary>
	private static async Task<bool> TryStartSessionForAsync(
		IWorkSessionCommandPort port, AppUserId actorId, AppUserId targetWorkerId, JobNodeId leafId)
	{
		try {
			_ = await port.StartSessionAsync(new() { Context = ContextFor(actorId), LeafWorkId = leafId, WorkedByUserId = targetWorkerId });
			return true;
		}
		catch (Exception ex) when (ex is InvariantViolationException or ConcurrencyConflictException
									   or PrerequisiteBlockedException or AuthorizationDeniedException) {
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
		await PrepareConnectionAsync(connection);
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
