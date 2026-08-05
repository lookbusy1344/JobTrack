namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;

/// <summary>
///     ADR 0058: the first time a leaf under an unacknowledged <c>job_request</c>'s subtree advances
///     into <see cref="Achievement.InProgress" /> or reaches a terminal achievement, the request is
///     auto-acknowledged as a side effect of that same write -- asserted identically against
///     PostgreSQL and SQLite by one thin sealed subclass per provider's own test project, same shape
///     as <see cref="JobRequestCommandPortContractTestsBase" />.
/// </summary>
public abstract class RequesterAutoAcknowledgementContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private readonly IDisposableTestDatabase database;

	protected RequesterAutoAcknowledgementContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	/// <summary>The current test database's provider-specific connection string.</summary>
	protected string ConnectionString => database.ConnectionString;

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Starting_work_on_an_unacknowledged_requests_leaf_auto_acknowledges_it()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var sessionPort = CreateSessionPort(database.ConnectionString);

		_ = await sessionPort.StartWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId, WorkedByUserId = jobManagerId });

		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Starting_work_writes_an_auto_acknowledge_audit_event()
	{
		var (_, jobManagerId, _, _, submitted) = await SeedSubmittedRequestAsync();
		var sessionPort = CreateSessionPort(database.ConnectionString);

		_ = await sessionPort.StartWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId, WorkedByUserId = jobManagerId });

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_request", EntityId = submitted.JobNodeId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().Contain(e => e.Operation == "auto-acknowledge-request");
	}

	[Fact]
	public async Task Starting_work_on_an_already_acknowledged_requests_leaf_does_not_add_a_second_acknowledgement()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		_ = await requestPort.AcknowledgeAsync(
			new() { Context = ContextFor(jobManagerId), NodeId = submitted.JobNodeId, Version = submitted.Version });
		var sessionPort = CreateSessionPort(database.ConnectionString);

		_ = await sessionPort.StartWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId, WorkedByUserId = jobManagerId });

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_request", EntityId = submitted.JobNodeId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Select(e => e.Operation).Should().NotContain("auto-acknowledge-request");
		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Cancelling_directly_from_waiting_on_an_unacknowledged_requests_leaf_auto_acknowledges_it()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var attached = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId });
		var achievementPort = CreateAchievementPort(database.ConnectionString);

		_ = await achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = submitted.JobNodeId,
			NewAchievement = Achievement.Cancelled,
			Reason = "No longer needed",
			Version = attached.Version,
		});

		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Completing_a_leaf_directly_from_waiting_on_an_unacknowledged_requests_leaf_auto_acknowledges_it()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var attached = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId });
		var sessionPort = CreateSessionPort(database.ConnectionString);

		_ = await sessionPort.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = submitted.JobNodeId,
			Version = attached.Version,
			ExpectedActiveSessions = [],
			FinalAchievement = Achievement.Cancelled,
		});

		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().NotBeNull();
	}

	/// <summary>
	///     By the time a leaf reaches a terminal achievement, <see cref="CompleteLeafAsync" /> or
	///     <see cref="SetAchievementAsync" /> has already auto-acknowledged its request (schema version
	///     0020's <c>job_request_no_reacknowledge</c> trigger makes acknowledgment immutable once set,
	///     so it can never regress to unacknowledged) -- <see cref="ReopenAndStartWorkAsync" />'s own
	///     hook can only ever see an already-acknowledged request in practice. This proves that call is
	///     a harmless no-op: reopening never adds a second <c>auto-acknowledge-request</c> audit event.
	/// </summary>
	[Fact]
	public async Task Reopening_and_starting_work_on_an_already_acknowledged_terminal_leaf_does_not_add_a_second_acknowledgement()
	{
		var (_, jobManagerId, _, _, submitted) = await SeedSubmittedRequestAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var attached = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId });
		var sessionPort = CreateSessionPort(database.ConnectionString);
		var completed = await sessionPort.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = submitted.JobNodeId,
			Version = attached.Version,
			ExpectedActiveSessions = [],
			FinalAchievement = Achievement.Cancelled,
		});

		_ = await sessionPort.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = submitted.JobNodeId,
			Version = completed.Version,
			Reason = "Requester asked to resume it",
			WorkedByUserId = jobManagerId,
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_request", EntityId = submitted.JobNodeId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Count(e => e.Operation == "auto-acknowledge-request").Should().Be(1);
	}

	/// <summary>
	///     Remediation plan §3.1 step 5: <c>job_request</c> anchors can nest (nothing in schema version
	///     0020 forbids anchoring a request at a node inside another request's subtree, and a move can
	///     produce one), so the walk must acknowledge the <em>nearest</em> enclosing anchor rather than
	///     whichever row an unordered ancestor-set match happened to return first.
	/// </summary>
	[Fact]
	public async Task Starting_work_acknowledges_the_nearest_enclosing_request_anchor_only()
	{
		var (requesterId, jobManagerId, holdingAreaId, requestPort, outer) = await SeedSubmittedRequestAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var innerAnchorId = await AddChildAsync(jobNodePort, jobManagerId, outer.JobNodeId, "Nested request anchor");
		await SeedRequestAnchorAsync(innerAnchorId, requesterId, holdingAreaId);
		var leaf = await AddLeafAsync(jobNodePort, jobManagerId, innerAnchorId, "Nested request leaf");

		_ = await CreateSessionPort(database.ConnectionString).StartWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.NodeId, WorkedByUserId = jobManagerId });

		var inner = await requestPort.GetDetailAsync(DetailRequest(requesterId, innerAnchorId));
		inner.AcknowledgedAt.Should().NotBeNull();
		var outerDetail = await requestPort.GetDetailAsync(DetailRequest(requesterId, outer.JobNodeId));
		outerDetail.AcknowledgedAt.Should().BeNull();
	}

	/// <summary>
	///     Remediation plan §3.2: <c>AddChildAsync</c>'s <see cref="CreateJobNodeRequest.BeginWork" />
	///     composite performs the same <see cref="Achievement.Waiting" /> -&gt;
	///     <see cref="Achievement.InProgress" /> transition <c>StartWorkAsync</c> does, so it owes the
	///     same ADR 0058 side effect. A later session start cannot repair an omission here, because
	///     <c>StartWorkAsync</c> auto-acknowledges only while performing that transition itself.
	/// </summary>
	[Fact]
	public async Task Creating_a_child_and_beginning_work_under_an_unacknowledged_request_auto_acknowledges_it()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();

		_ = await CreateAndBeginWorkAsync(jobManagerId, submitted.JobNodeId, ContextFor(jobManagerId));

		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().NotBeNull();
		var acknowledgedBy = await ReadAcknowledgedByUserIdAsync(submitted.JobNodeId);
		acknowledgedBy.Should().Be(jobManagerId.Value);
	}

	[Fact]
	public async Task Creating_a_child_and_beginning_work_acknowledges_under_the_commands_own_correlation_id()
	{
		var (_, jobManagerId, _, _, submitted) = await SeedSubmittedRequestAsync();
		var commandContext = ContextFor(jobManagerId);

		_ = await CreateAndBeginWorkAsync(jobManagerId, submitted.JobNodeId, commandContext);

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { CorrelationId = commandContext.CorrelationId }, null, AuditSearchTestDefaults.AllRowsLimit);

		var operations = audit.Events.Select(e => e.Operation).ToList();
		operations.Should().Contain(["create-job-node", "set-achievement", "start-work-session", "auto-acknowledge-request"]);
		operations.Count(operation => operation == "auto-acknowledge-request").Should().Be(1);
	}

	/// <summary>
	///     Remediation plan §3.2 step 1's injected-failure case: adding a child under a node that already
	///     holds <c>LeafWork</c> violates leaf/branch exclusivity. On PostgreSQL that trigger is deferred
	///     to <c>COMMIT</c> (schema version 0006), so the acknowledgement has already been written by the
	///     time the transaction aborts — which is exactly what proves the conditional update runs inside
	///     the create's transaction rather than auto-committing beside it. On SQLite the same trigger is
	///     immediate and aborts earlier; the observable contract asserted here is identical on both.
	/// </summary>
	[Fact]
	public async Task A_failed_create_and_begin_work_leaves_the_request_unacknowledged()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId });

		var act = () => CreateAndBeginWorkAsync(jobManagerId, submitted.JobNodeId, ContextFor(jobManagerId));

		_ = await act.Should().ThrowAsync<JobTrackException>();
		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().BeNull();
	}

	/// <summary>
	///     <c>ImportSubtreeAsync</c> replays recorded history by writing an imported leaf's final
	///     achievement directly. That final state is still a first transition into
	///     <see cref="Achievement.InProgress" /> or a terminal state under the enclosing request, so ADR
	///     0058 applies: a requester must not see <c>Submitted</c> beside a job the import already
	///     recorded as finished. The import keeps its own <c>import-leaf-work</c> audit event; only the
	///     acknowledgement is shared.
	/// </summary>
	[Fact]
	public async Task Importing_a_worked_leaf_under_an_unacknowledged_request_auto_acknowledges_it()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var commandContext = ContextFor(jobManagerId);
		var startedAt = SystemClock.Instance.GetCurrentInstant() - Duration.FromHours(1);

		_ = await CreateJobNodePort(database.ConnectionString).ImportSubtreeAsync(new() {
			Context = commandContext,
			ParentId = submitted.JobNodeId,
			Nodes = new([
				new() {
					LocalId = 1,
					Description = "Imported completed work",
					OwnerUserId = jobManagerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = jobManagerId,
						StartedAt = startedAt,
						FinishedAt = startedAt + Duration.FromMinutes(30),
						Achievement = Achievement.Success,
					},
				},
			]),
		});

		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().NotBeNull();

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { CorrelationId = commandContext.CorrelationId }, null, AuditSearchTestDefaults.AllRowsLimit);

		var operations = audit.Events.Select(e => e.Operation).ToList();
		operations.Should().Contain(["import-leaf-work", "auto-acknowledge-request"]);
		operations.Count(operation => operation == "auto-acknowledge-request").Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobRequestCommandPort CreateRequestPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IWorkSessionCommandPort CreateSessionPort(string connectionString);

	internal abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>
	///     Remediation plan §3.1 steps 1–2: two independent connections start work on two different
	///     leaves under the same unacknowledged request at the same instant. Both commands must succeed
	///     — the acknowledgement is a side effect, so the loser of the race must silently no-op rather
	///     than roll its own leaf's session back with a concurrency conflict — and the request must end
	///     up acknowledged exactly once, with exactly one <c>auto-acknowledge-request</c> audit event.
	/// </summary>
	protected async Task AssertConcurrentFirstWorkAcknowledgesExactlyOnceAsync()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var (first, second) = await SeedTwoLeavesUnderRequestAsync(jobManagerId, submitted.JobNodeId);

		await RunSimultaneouslyAsync(
			() => CreateSessionPort(database.ConnectionString).StartWorkAsync(
				new() { Context = ContextFor(jobManagerId), JobNodeId = first.NodeId, WorkedByUserId = jobManagerId }),
			() => CreateSessionPort(database.ConnectionString).StartWorkAsync(
				new() { Context = ContextFor(jobManagerId), JobNodeId = second.NodeId, WorkedByUserId = jobManagerId }));

		await AssertAcknowledgedExactlyOnceAsync(requestPort, requesterId, submitted.JobNodeId);
	}

	/// <summary>
	///     The terminal-achievement half of <see cref="AssertConcurrentFirstWorkAcknowledgesExactlyOnceAsync" />:
	///     two leaves under one unacknowledged request reach a terminal achievement simultaneously.
	/// </summary>
	protected async Task AssertConcurrentTerminalOutcomeAcknowledgesExactlyOnceAsync()
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var (first, second) = await SeedTwoLeavesUnderRequestAsync(jobManagerId, submitted.JobNodeId);

		await RunSimultaneouslyAsync(
			() => CreateAchievementPort(database.ConnectionString).SetAchievementAsync(new() {
				Context = ContextFor(jobManagerId),
				JobNodeId = first.NodeId,
				NewAchievement = Achievement.Cancelled,
				Reason = "No longer needed",
				Version = first.Version,
			}),
			() => CreateAchievementPort(database.ConnectionString).SetAchievementAsync(new() {
				Context = ContextFor(jobManagerId),
				JobNodeId = second.NodeId,
				NewAchievement = Achievement.Cancelled,
				Reason = "No longer needed",
				Version = second.Version,
			}));

		await AssertAcknowledgedExactlyOnceAsync(requestPort, requesterId, submitted.JobNodeId);
	}

	/// <summary>
	///     PostgreSQL proof for remediation plan §3.1 step 2: both terminal transitions are held at the
	///     request-row update boundary until both transactions have read the original unacknowledged
	///     state. The former tracked compare-and-swap implementation therefore fails deterministically;
	///     the conditional update lets both commands complete with one acknowledgement.
	/// </summary>
	internal async Task AssertDeterministicConcurrentTerminalOutcomeAcknowledgesExactlyOnceAsync(
		Func<DbCommandInterceptor, IAchievementCommandPort> createPort)
	{
		var (requesterId, jobManagerId, _, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var (first, second) = await SeedTwoLeavesUnderRequestAsync(jobManagerId, submitted.JobNodeId);
		var interceptor =
			new TwoPartyNonQueryCommandBarrierInterceptor(sql => sql.Contains("UPDATE job_request", StringComparison.OrdinalIgnoreCase));

		await Task.WhenAll(
			SetCancelledAsync(createPort(interceptor), jobManagerId, first),
			SetCancelledAsync(createPort(interceptor), jobManagerId, second));

		await AssertAcknowledgedExactlyOnceAsync(requestPort, requesterId, submitted.JobNodeId);
	}

	/// <summary>
	///     Releases both operations from one gate so they contend inside the database rather than
	///     merely running back to back, and surfaces either side's failure as the test's failure.
	/// </summary>
	private static async Task RunSimultaneouslyAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
	{
		using var gate = new Barrier(2);
		_ = await Task.WhenAll(
			Task.Run(() => {
				gate.SignalAndWait();
				return first();
			}),
			Task.Run(() => {
				gate.SignalAndWait();
				return second();
			}));
	}

	private async Task AssertAcknowledgedExactlyOnceAsync(
		IJobRequestCommandPort requestPort, AppUserId requesterId, JobNodeId anchorId)
	{
		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, anchorId));
		detail.AcknowledgedAt.Should().NotBeNull();

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_request", EntityId = anchorId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Count(e => e.Operation == "auto-acknowledge-request").Should().Be(1);
	}

	private async Task<(SeededLeaf First, SeededLeaf Second)> SeedTwoLeavesUnderRequestAsync(
		AppUserId jobManagerId, JobNodeId anchorId)
	{
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		return (await AddLeafAsync(jobNodePort, jobManagerId, anchorId, "First child"),
			await AddLeafAsync(jobNodePort, jobManagerId, anchorId, "Second child"));
	}

	private static Task<LeafWorkResult> SetCancelledAsync(
		IAchievementCommandPort port, AppUserId actorId, SeededLeaf leaf) =>
		port.SetAchievementAsync(new() {
			Context = ContextFor(actorId),
			JobNodeId = leaf.NodeId,
			NewAchievement = Achievement.Cancelled,
			Reason = "No longer needed",
			Version = leaf.Version,
		});

	private static async Task<SeededLeaf> AddLeafAsync(
		IJobNodeCommandPort port, AppUserId actorId, JobNodeId parentId, string description)
	{
		var nodeId = await AddChildAsync(port, actorId, parentId, description);
		var attached = await port.AttachLeafWorkAsync(new() { Context = ContextFor(actorId), JobNodeId = nodeId });
		return new(nodeId, attached.Version);
	}

	private Task<JobNodeResult> CreateAndBeginWorkAsync(AppUserId actorId, JobNodeId parentId, CommandContext commandContext) =>
		CreateJobNodePort(database.ConnectionString).AddChildAsync(new() {
			Context = commandContext,
			ParentId = parentId,
			Description = "Created and started in one transaction",
			OwnerUserId = actorId,
			Priority = Priority.Medium,
			BeginWork = new() { WorkedByUserId = actorId },
		});

	private static async Task<JobNodeId> AddChildAsync(
		IJobNodeCommandPort port, AppUserId actorId, JobNodeId parentId, string description)
	{
		var node = await port.AddChildAsync(new() {
			Context = ContextFor(actorId),
			ParentId = parentId,
			Description = description,
			OwnerUserId = actorId,
			Priority = Priority.Medium,
		});

		return node.Id;
	}

	private static CommandContext ContextFor(AppUserId actorId) => new() { Actor = actorId, CorrelationId = Guid.NewGuid() };

	private static GetJobRequestDetailRequest DetailRequest(AppUserId actorId, JobNodeId nodeId) =>
		new() { Context = ContextFor(actorId), NodeId = nodeId };

	/// <summary>
	///     Deploys the schema, bootstraps a root/administrator, seeds one requester and one job manager,
	///     an eligible holding area, and a request submitted by the requester into it -- left
	///     deliberately unacknowledged for each test to drive its own trigger.
	/// </summary>
	private async Task<(AppUserId RequesterId, AppUserId JobManagerId, RequestHoldingAreaId HoldingAreaId,
		IJobRequestCommandPort RequestPort, JobRequestResult Submitted)> SeedSubmittedRequestAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		_ = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		var requesterId = await SeedEmployeeAsync("Rita Requester", "rita.requester.autoack", EmployeeRole.Requester);
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager.autoack", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync();

		var requestPort = CreateRequestPort(database.ConnectionString);
		var submitted = await requestPort.SubmitAsync(new() {
			Context = ContextFor(requesterId),
			HoldingAreaId = holdingAreaId,
			Description = "Printer will not turn on",
		});

		return (requesterId, jobManagerId, holdingAreaId, requestPort, submitted);
	}

	/// <summary>
	///     Inserts a second, unacknowledged <c>job_request</c> anchored at an existing node — the nested
	///     anchor no port exposes directly, but which schema version 0020 permits and a move can create.
	/// </summary>
	private async Task SeedRequestAnchorAsync(JobNodeId anchorId, AppUserId requesterId, RequestHoldingAreaId holdingAreaId)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_request (job_node_id, requester_user_id, holding_area_id, submitted_at)
							  VALUES (@jobNodeId, @requesterUserId, @holdingAreaId, @submittedAt);
							  """;
		AddParameter(command, "@jobNodeId", anchorId.Value);
		AddParameter(command, "@requesterUserId", requesterId.Value);
		AddParameter(command, "@holdingAreaId", holdingAreaId.Value);
		AddParameter(command, "@submittedAt", EncodeInstant(DateTimeOffset.UtcNow));
		_ = await command.ExecuteNonQueryAsync();
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

		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();

		return appUserId;
	}

	private async Task<RequestHoldingAreaId> SeedHoldingAreaAsync()
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var rootCommand = connection.CreateCommand();
		rootCommand.CommandText = "SELECT id FROM job_node WHERE parent_id IS NULL;";
		var rootId = Convert.ToInt64(await rootCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		await using var nodeCommand = connection.CreateCommand();
		nodeCommand.CommandText = """
								  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								  VALUES (@parentId, 'Holding area', @postedByUserId, @postedByUserId, @priorityId, @postedAt)
								  RETURNING id;
								  """;
		AddParameter(nodeCommand, "@parentId", rootId);
		AddParameter(nodeCommand, "@postedByUserId", rootId);
		AddParameter(nodeCommand, "@priorityId", PriorityMedium);
		AddParameter(nodeCommand, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));
		var jobNodeId = Convert.ToInt64(await nodeCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		await using var holdingAreaCommand = connection.CreateCommand();
		holdingAreaCommand.CommandText = """
										 INSERT INTO request_holding_area
										    (job_node_id, department_id, name, default_priority_id, default_owner_user_id, is_active)
										 VALUES
										    (@jobNodeId, NULL, 'IT Intake', @priorityId, NULL, @isActive)
										 RETURNING id;
										 """;
		AddParameter(holdingAreaCommand, "@jobNodeId", jobNodeId);
		AddParameter(holdingAreaCommand, "@priorityId", PriorityMedium);
		AddParameter(holdingAreaCommand, "@isActive", true);

		return new(Convert.ToInt64(await holdingAreaCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
	}

	/// <summary>
	///     Reads <c>job_request.acknowledged_by_user_id</c> directly: the requester-facing detail
	///     projection deliberately exposes only <c>acknowledged_at</c>, but ADR 0058 requires the
	///     triggering command's own actor to be recorded as the acknowledger.
	/// </summary>
	private async Task<long?> ReadAcknowledgedByUserIdAsync(JobNodeId anchorId)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT acknowledged_by_user_id FROM job_request WHERE job_node_id = @jobNodeId;";
		AddParameter(command, "@jobNodeId", anchorId.Value);
		var value = await command.ExecuteScalarAsync();

		return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
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

	/// <summary>A created child node with <c>LeafWork</c> attached, and that leaf work's current version.</summary>
	private sealed record SeededLeaf(JobNodeId NodeId, long Version);
}
