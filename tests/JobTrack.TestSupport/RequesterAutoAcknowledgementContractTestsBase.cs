namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;

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

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Starting_work_on_an_unacknowledged_requests_leaf_auto_acknowledges_it()
	{
		var (requesterId, jobManagerId, requestPort, submitted) = await SeedSubmittedRequestAsync();
		var sessionPort = CreateSessionPort(database.ConnectionString);

		_ = await sessionPort.StartWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = submitted.JobNodeId, WorkedByUserId = jobManagerId });

		var detail = await requestPort.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		detail.AcknowledgedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Starting_work_writes_an_auto_acknowledge_audit_event()
	{
		var (_, jobManagerId, _, submitted) = await SeedSubmittedRequestAsync();
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
		var (requesterId, jobManagerId, requestPort, submitted) = await SeedSubmittedRequestAsync();
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
		var (requesterId, jobManagerId, requestPort, submitted) = await SeedSubmittedRequestAsync();
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
		var (requesterId, jobManagerId, requestPort, submitted) = await SeedSubmittedRequestAsync();
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
		var (_, jobManagerId, _, submitted) = await SeedSubmittedRequestAsync();
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

	private static CommandContext ContextFor(AppUserId actorId) => new() { Actor = actorId, CorrelationId = Guid.NewGuid() };

	private static GetJobRequestDetailRequest DetailRequest(AppUserId actorId, JobNodeId nodeId) =>
		new() { Context = ContextFor(actorId), NodeId = nodeId };

	/// <summary>
	///     Deploys the schema, bootstraps a root/administrator, seeds one requester and one job manager,
	///     an eligible holding area, and a request submitted by the requester into it -- left
	///     deliberately unacknowledged for each test to drive its own trigger.
	/// </summary>
	private async Task<(AppUserId RequesterId, AppUserId JobManagerId, IJobRequestCommandPort RequestPort, JobRequestResult Submitted)>
		SeedSubmittedRequestAsync()
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

		return (requesterId, jobManagerId, requestPort, submitted);
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
}
