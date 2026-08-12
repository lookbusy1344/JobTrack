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
///     Shared contract for <see cref="IWorkSessionQueryPort" /> (plan §8.5 slice 4), asserted
///     identically against PostgreSQL and SQLite by one thin sealed subclass per provider's own test
///     project -- same shape as <see cref="JobBrowseQueryPortContractTestsBase" />. Seeds a leaf with
///     attached <c>LeafWork</c> and two historical sessions via the real
///     <see cref="IInstallationBootstrapPort" />/<see cref="IJobNodeCommandPort" />/<see cref="IWorkSessionCommandPort" />,
///     then corrects both sessions to known instants so ordering assertions are deterministic
///     regardless of real-clock resolution.
/// </summary>
public abstract class WorkSessionQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected WorkSessionQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetSessionsAsync_returns_the_workers_sessions_most_recent_first()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 2, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, workerId);

		result.Sessions.Select(s => s.StartedAt).Should()
			  .ContainInOrder(Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 1, 8, 0));
	}

	[Fact]
	public async Task GetSessionsAsync_bounds_results_by_offset_and_limit_preserving_most_recent_first_order()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 2, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var firstPage = await port.GetSessionsAsync(administratorId, leafId, workerId, 0, 1);
		var secondPage = await port.GetSessionsAsync(administratorId, leafId, workerId, 1, 1);

		firstPage.Sessions.Select(s => s.StartedAt).Should().ContainSingle().Which.Should().Be(Instant.FromUtc(2026, 1, 2, 8, 0));
		secondPage.Sessions.Select(s => s.StartedAt).Should().ContainSingle().Which.Should().Be(Instant.FromUtc(2026, 1, 1, 8, 0));
	}

	[Fact]
	public async Task GetSessionsAsync_does_not_return_another_workers_sessions()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, administratorId);

		result.Sessions.Should().BeEmpty();
	}

	/// <summary>
	///     A <see langword="null" /> worker filter means "every worker's sessions on this leaf" (ADR 0041)
	///     — the default the sessions panel now loads with, narrowing to one worker being the follow-up
	///     filter rather than the entry point. Ordering must stay most-recent-first across the union of
	///     workers, not merely within one worker's own sessions.
	/// </summary>
	[Fact]
	public async Task GetSessionsAsync_without_a_worker_filter_returns_every_workers_sessions_most_recent_first()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		await SeedCorrectedSessionAsync(administratorId, administratorId, leafId, Instant.FromUtc(2026, 1, 2, 8, 0),
			Instant.FromUtc(2026, 1, 2, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, null);

		result.Sessions.Should().HaveCount(2);
		result.Sessions.Select(s => s.WorkedByUserId).Should().Contain([workerId, administratorId]);
		result.Sessions.Select(s => s.StartedAt).Should()
			  .ContainInOrder(Instant.FromUtc(2026, 1, 2, 8, 0), Instant.FromUtc(2026, 1, 1, 8, 0));
	}

	[Fact]
	public async Task GetSessionsAsync_returns_the_actors_current_roles()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetSessionsAsync(administratorId, leafId, workerId);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetSessionsAsync_throws_for_a_nonexistent_actor()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetSessionsAsync(new(administratorId.Value + 999), leafId, workerId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetSessionsAsync_throws_for_a_nonexistent_leaf()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetSessionsAsync(administratorId, new(leafId.Value + 999), workerId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetActiveSessionsAsync_returns_the_actors_own_unfinished_session_among_the_given_leaves()
	{
		var (_, workerId, leafId) = await SeedWorkedLeafAsync();
		var sessionCommandPort = CreateSessionCommandPort(database.ConnectionString);
		var active = await sessionCommandPort.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(workerId, [leafId]);

		result.Sessions.Should().ContainSingle(s => s.Id == active.Id);
	}

	[Fact]
	public async Task GetActiveSessionsAsync_does_not_return_a_finished_session()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 8, 0), Instant.FromUtc(2026, 1, 1, 9, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(workerId, [leafId]);

		result.Sessions.Should().BeEmpty();
	}

	/// <summary>
	///     The port itself applies no actor-based filtering (matching <see cref="GetSessionsAsync" />'s
	///     "every worker" default, ADR 0041) -- it returns every unfinished session among the given
	///     leaves regardless of who is querying or who worked it. <c>JobQueries</c> is the layer that
	///     narrows this to what the querying actor may see.
	/// </summary>
	[Fact]
	public async Task GetActiveSessionsAsync_returns_every_workers_active_session_among_the_given_leaves()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var sessionCommandPort = CreateSessionCommandPort(database.ConnectionString);
		var active = await sessionCommandPort.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(administratorId, [leafId]);

		result.Sessions.Should().ContainSingle(s => s.Id == active.Id);
	}

	[Fact]
	public async Task GetActiveSessionsAsync_with_no_leaves_returns_an_empty_result_without_throwing()
	{
		var (_, workerId, _) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(workerId, []);

		result.Sessions.Should().BeEmpty();
	}

	[Fact]
	public async Task GetActiveSessionsAsync_returns_the_actors_current_roles()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetActiveSessionsAsync(administratorId, [leafId]);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetManageCapabilitiesAsync_reports_the_owning_workers_control_of_their_own_leaf()
	{
		var (_, workerId, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetManageCapabilitiesAsync(workerId, [leafId]);

		result.ControlledLeafWorkIds.Should().Contain(leafId);
	}

	[Fact]
	public async Task GetManageCapabilitiesAsync_does_not_report_control_for_a_worker_who_does_not_own_the_leaf()
	{
		var (_, _, leafId) = await SeedWorkedLeafAsync();
		var otherWorkerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Other Worker", "other.worker.capability", EmployeeRole.Worker);
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetManageCapabilitiesAsync(otherWorkerId, [leafId]);

		result.ControlledLeafWorkIds.Should().NotContain(leafId);
	}

	[Fact]
	public async Task GetManageCapabilitiesAsync_returns_the_actors_current_roles()
	{
		var (administratorId, _, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetManageCapabilitiesAsync(administratorId, [leafId]);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetManageCapabilitiesAsync_with_no_leaves_returns_an_empty_result_without_throwing()
	{
		var (_, workerId, _) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetManageCapabilitiesAsync(workerId, []);

		result.ControlledLeafWorkIds.Should().BeEmpty();
	}

	[Fact]
	public async Task GetManageCapabilitiesAsync_throws_for_a_nonexistent_actor()
	{
		var (administratorId, _, leafId) = await SeedWorkedLeafAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetManageCapabilitiesAsync(new(administratorId.Value + 999), [leafId]);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	/// <summary>
	///     ADR 0044/Stage 4: the batched ancestor-ownership walk backing this capability must stay at a
	///     fixed round-trip count as the number of requested leaves grows, never scaling per leaf (no
	///     N+1 -- the exact defect a per-row <c>CanManage</c> re-check would reintroduce).
	/// </summary>
	[Fact]
	public async Task GetManageCapabilitiesAsync_executes_a_fixed_number_of_round_trips_regardless_of_leaf_count()
	{
		var (administratorId, workerId, firstLeafId) = await SeedWorkedLeafAsync();
		var jobCommandPort = CreateJobCommandPort(database.ConnectionString);
		var rootId = await FindRootAsync();
		var leafIds = new List<JobNodeId> {
			firstLeafId,
		};
		for (var i = 0; i < 10; ++i) {
			var node = await jobCommandPort.AddChildAsync(new() {
				Context = ContextFor(administratorId),
				ParentId = rootId,
				Description = $"Additional leaf {i}",
				OwnerUserId = workerId,
				Priority = Priority.Medium,
			});
			_ = await jobCommandPort.AttachLeafWorkAsync(new() {
				Context = ContextFor(administratorId),
				JobNodeId = node.Id,
			});
			leafIds.Add(node.Id);
		}

		var interceptor = new CommandCountInterceptor();
		var port = CreateQueryPortWithCommandCounter(database.ConnectionString, interceptor);

		_ = await port.GetManageCapabilitiesAsync(workerId, [.. leafIds]);

		interceptor.Count.Should().Be(
			3, "two queries for the actor's identity/roles and one batched ancestor-ownership walk, regardless of leaf count");
	}

	/// <summary>
	///     The concurrent-work read (ADR 0041 visibility, spec §4.4's deliberate cross-leaf overlap):
	///     the subject leaf's own sessions plus the same worker's intersecting sessions elsewhere, which
	///     <c>ConcurrentWorkCalculator</c> then aggregates.
	/// </summary>
	[Fact]
	public async Task GetConcurrentSessionsAsync_returns_the_subject_sessions_and_the_same_workers_overlapping_session_elsewhere()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var otherLeafId = await SeedAdditionalWorkedLeafAsync(administratorId, workerId, "Fit windows");
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 12, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, otherLeafId, Instant.FromUtc(2026, 1, 1, 11, 0),
			Instant.FromUtc(2026, 1, 1, 13, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetConcurrentSessionsAsync(leafId, Instant.FromUtc(2026, 1, 2, 0, 0), 100, 100);

		result.SubjectSessions.Select(s => s.NodeId).Should().AllBeEquivalentTo(leafId);
		result.ConcurrentSessions.Should().ContainSingle().Which.NodeId.Should().Be(otherLeafId);
		result.IsTruncated.Should().BeFalse();
	}

	[Fact]
	public async Task GetConcurrentSessionsAsync_does_not_return_a_session_elsewhere_that_does_not_overlap()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var otherLeafId = await SeedAdditionalWorkedLeafAsync(administratorId, workerId, "Fit windows");
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 11, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, otherLeafId, Instant.FromUtc(2026, 1, 1, 11, 0),
			Instant.FromUtc(2026, 1, 1, 13, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetConcurrentSessionsAsync(leafId, Instant.FromUtc(2026, 1, 2, 0, 0), 100, 100);

		result.ConcurrentSessions.Should().BeEmpty("a session that merely touches the subject's at a boundary does not overlap it");
	}

	[Fact]
	public async Task GetConcurrentSessionsAsync_does_not_return_another_workers_session_elsewhere()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var otherLeafId = await SeedAdditionalWorkedLeafAsync(administratorId, administratorId, "Fit windows");
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 12, 0));
		await SeedCorrectedSessionAsync(administratorId, administratorId, otherLeafId, Instant.FromUtc(2026, 1, 1, 9, 0),
			Instant.FromUtc(2026, 1, 1, 12, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetConcurrentSessionsAsync(leafId, Instant.FromUtc(2026, 1, 2, 0, 0), 100, 100);

		result.ConcurrentSessions.Should().BeEmpty("concurrency is per worker -- two people working at once share no allocation");
	}

	[Fact]
	public async Task GetConcurrentSessionsAsync_bounds_an_unfinished_session_at_the_as_of_instant()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var otherLeafId = await SeedAdditionalWorkedLeafAsync(administratorId, workerId, "Fit windows");
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 17, 0));
		await SeedUnfinishedSessionAsync(administratorId, workerId, otherLeafId, Instant.FromUtc(2026, 1, 1, 10, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetConcurrentSessionsAsync(leafId, Instant.FromUtc(2026, 1, 1, 12, 0), 100, 100);

		result.ConcurrentSessions.Should().ContainSingle().Which.Interval.End.Should().Be(Instant.FromUtc(2026, 1, 1, 12, 0));
	}

	[Fact]
	public async Task GetConcurrentSessionsAsync_reports_truncation_when_the_concurrent_cap_is_reached()
	{
		var (administratorId, workerId, leafId) = await SeedWorkedLeafAsync();
		var firstOtherLeafId = await SeedAdditionalWorkedLeafAsync(administratorId, workerId, "Fit windows");
		var secondOtherLeafId = await SeedAdditionalWorkedLeafAsync(administratorId, workerId, "Paint hallway");
		await SeedCorrectedSessionAsync(administratorId, workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 17, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, firstOtherLeafId, Instant.FromUtc(2026, 1, 1, 10, 0),
			Instant.FromUtc(2026, 1, 1, 11, 0));
		await SeedCorrectedSessionAsync(administratorId, workerId, secondOtherLeafId, Instant.FromUtc(2026, 1, 1, 12, 0),
			Instant.FromUtc(2026, 1, 1, 13, 0));
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetConcurrentSessionsAsync(leafId, Instant.FromUtc(2026, 1, 2, 0, 0), 100, 1);

		result.ConcurrentSessions.Should().ContainSingle();
		result.IsTruncated.Should().BeTrue("a capped load reports a floor, never a total presented as complete");
	}

	[Fact]
	public async Task GetConcurrentSessionsAsync_returns_nothing_for_a_node_with_no_sessions()
	{
		var (administratorId, workerId, _) = await SeedWorkedLeafAsync();
		var unworkedLeafId = await SeedAdditionalWorkedLeafAsync(administratorId, workerId, "Untouched");
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetConcurrentSessionsAsync(unworkedLeafId, Instant.FromUtc(2026, 1, 2, 0, 0), 100, 100);

		result.SubjectSessions.Should().BeEmpty();
		result.ConcurrentSessions.Should().BeEmpty();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobCommandPort(string connectionString);

	internal abstract IWorkSessionCommandPort CreateSessionCommandPort(string connectionString);

	internal abstract IWorkSessionQueryPort CreateQueryPort(string connectionString);

	/// <summary>Stage 4 efficiency-guard seam: a query port wired with <paramref name="interceptor" /> attached to its <c>DbContext</c>.</summary>
	internal abstract IWorkSessionQueryPort CreateQueryPortWithCommandCounter(string connectionString, CommandCountInterceptor interceptor);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	/// <summary>Seeds root -&gt; leaf "Pour foundation" (worker-owned, LeafWork attached), owned and worked by a seeded worker.</summary>
	private async Task<(AppUserId AdministratorId, AppUserId WorkerId, JobNodeId LeafId)> SeedWorkedLeafAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;

		var workerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Grace Hopper", "grace.hopper", EmployeeRole.Worker);

		var jobCommandPort = CreateJobCommandPort(database.ConnectionString);
		var leaf = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Pour foundation",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobCommandPort.AttachLeafWorkAsync(new() {
			Context = ContextFor(administratorId),
			JobNodeId = leaf.Id,
		});

		return (administratorId, workerId, leaf.Id);
	}

	/// <summary>Seeds a second worked leaf under the root, so cross-leaf concurrency has somewhere to happen.</summary>
	private async Task<JobNodeId> SeedAdditionalWorkedLeafAsync(AppUserId administratorId, AppUserId ownerId, string description)
	{
		var jobCommandPort = CreateJobCommandPort(database.ConnectionString);
		var leaf = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = await FindRootAsync(),
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await jobCommandPort.AttachLeafWorkAsync(new() {
			Context = ContextFor(administratorId),
			JobNodeId = leaf.Id,
		});

		return leaf.Id;
	}

	/// <summary>Starts a session at a known past instant and leaves it running, so clipping at <c>asOf</c> is observable.</summary>
	private async Task SeedUnfinishedSessionAsync(AppUserId administratorId, AppUserId workerId, JobNodeId leafId, Instant startedAt)
	{
		var sessionCommandPort = CreateSessionCommandPort(database.ConnectionString);
		_ = await sessionCommandPort.StartSessionAsync(new() {
			Context = ContextFor(administratorId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = startedAt,
		});
	}

	private async Task SeedCorrectedSessionAsync(
		AppUserId administratorId, AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var sessionCommandPort = CreateSessionCommandPort(database.ConnectionString);
		var started = await sessionCommandPort.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var finished = await sessionCommandPort.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = started.Id,
			Version = started.Version,
		});
		_ = await sessionCommandPort.CorrectSessionAsync(new() {
			Context = ContextFor(administratorId),
			SessionId = finished.Id,
			StartedAt = startedAt,
			FinishedAt = finishedAt,
			Reason = "Backdated for deterministic test ordering.",
			Version = finished.Version,
		});
	}



	private async Task<JobNodeId> FindRootAsync()
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT id FROM job_node WHERE parent_id IS NULL;";
		return new(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
	}
}
