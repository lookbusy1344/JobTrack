namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;

/// <summary>
///     Shared contract for <see cref="IWorkSessionCommandPort.StartWorkAsync" /> -- the one-click
///     composite of attach-if-missing, advance-to-<see cref="Achievement.InProgress" />, and start a
///     session, all inside one transaction -- asserted identically against PostgreSQL and SQLite by one
///     thin sealed subclass per provider's own test project, same shape as
///     <see cref="WorkSessionCommandPortContractTestsBase" />.
/// </summary>
public abstract class StartWorkCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected StartWorkCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Starting_work_on_a_fresh_leaf_attaches_leaf_work_and_advances_it_to_in_progress()
	{
		var (_, _, workerId, bareLeafId) = await SeedBareLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = bareLeafId, WorkedByUserId = workerId });

		result.LeafWorkId.Should().Be(bareLeafId);
		result.WorkedByUserId.Should().Be(workerId);
		result.FinishedAt.Should().BeNull();

		var leafWorkPort = CreateLeafWorkQueryPort(database.ConnectionString);
		var leafWork = await leafWorkPort.GetLeafWorkAsync(bareLeafId);
		leafWork.Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task Starting_work_writes_an_attach_and_a_start_audit_event()
	{
		var (_, _, workerId, bareLeafId) = await SeedBareLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var result = await port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = bareLeafId, WorkedByUserId = workerId });

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var leafWorkAudit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "leaf_work", EntityId = bareLeafId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);
		var sessionAudit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "work_session", EntityId = result.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		leafWorkAudit.Events.Select(e => e.Operation).Should().BeEquivalentTo("attach-leaf-work", "set-achievement");
		sessionAudit.Events.Select(e => e.Operation).Should().BeEquivalentTo("start-work-session");
	}

	[Fact]
	public async Task Starting_work_on_an_already_attached_waiting_leaf_advances_it_to_in_progress()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		_ = await port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });

		var leafWorkPort = CreateLeafWorkQueryPort(database.ConnectionString);
		var leafWork = await leafWorkPort.GetLeafWorkAsync(leafId);
		leafWork.Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task Starting_work_when_already_in_progress_from_another_workers_session_starts_a_new_session()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.startwork", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		_ = await port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leafId, WorkedByUserId = workerId });

		var result = await port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leafId, WorkedByUserId = otherWorkerId });

		result.WorkedByUserId.Should().Be(otherWorkerId);
		var leafWorkPort = CreateLeafWorkQueryPort(database.ConnectionString);
		(await leafWorkPort.GetLeafWorkAsync(leafId)).Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task Starting_work_blocked_by_an_unsatisfied_prerequisite_throws_prerequisite_blocked_and_persists_nothing()
	{
		var (rootId, jobManagerId, workerId, requiredLeaf) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var dependentLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Blocked bare leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = requiredLeaf,
			DependentJobId = dependentLeaf.Id,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = dependentLeaf.Id, WorkedByUserId = workerId });

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();

		var leafWorkPort = CreateLeafWorkQueryPort(database.ConnectionString);
		var getLeafWork = () => leafWorkPort.GetLeafWorkAsync(dependentLeaf.Id);
		await getLeafWork.Should().ThrowAsync<EntityNotFoundException>("a blocked start must roll back the attach it performed too");
	}

	[Fact]
	public async Task Starting_work_a_second_time_for_the_same_worker_and_leaf_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		_ = await port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-already-active");
	}

	[Fact]
	public async Task A_worker_cannot_start_work_for_another_worker()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.startwork.auth", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(otherWorkerId), JobNodeId = leafId, WorkedByUserId = workerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Starting_work_on_a_node_that_does_not_exist_throws_not_found()
	{
		var (_, jobManagerId, _, _) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = new(999_999), WorkedByUserId = jobManagerId });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Starting_work_on_the_root_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _, _) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = rootId, WorkedByUserId = jobManagerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-is-root-cannot-attach-leaf-work");
	}

	[Fact]
	public async Task Starting_work_on_a_branch_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var branch = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Branch",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Child of branch",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = branch.Id, WorkedByUserId = jobManagerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-children-cannot-attach-leaf-work");
	}

	[Fact]
	public async Task Starting_work_on_an_archived_leaf_throws_an_invariant_violation()
	{
		// ADR 0044: archiving a bare planning node (no LeafWork, so no active session to block it) is
		// itself permitted; StartWorkAsync must still refuse to attach/start against it afterwards.
		var (_, jobManagerId, workerId, bareLeafId) = await SeedBareLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.ArchiveAsync(new() { Context = ContextFor(jobManagerId), NodeId = bareLeafId, Version = 1 });
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = bareLeafId, WorkedByUserId = workerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-leaf-closed");
	}

	// ADR 0044 Stage 6 / plan §2.5: authorization matrix for starting work on behalf of another
	// worker (the "Start for..." disclosure posts through this same StartWorkAsync path).

	[Fact]
	public async Task An_administrator_can_start_work_for_another_worker_regardless_of_ownership()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.startfor.admin", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		// jobManagerId is the bootstrap administrator (also separately granted JobManager) and owns
		// neither this leaf nor its ancestors.
		var result = await port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leafId, WorkedByUserId = otherWorkerId });

		result.WorkedByUserId.Should().Be(otherWorkerId);
	}

	[Fact]
	public async Task A_job_manager_who_is_not_administrator_can_start_work_for_another_worker()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var jobManagerOnlyId = await SeedEmployeeAsync("Plain Manager", "plain.manager.startfor", EmployeeRole.JobManager);
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartWorkAsync(new() { Context = ContextFor(jobManagerOnlyId), JobNodeId = leafId, WorkedByUserId = workerId });

		result.WorkedByUserId.Should().Be(workerId);
	}

	[Fact]
	public async Task A_direct_owner_can_start_work_for_another_worker_on_their_own_leaf()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.startfor.owner", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		// workerId owns leafId (SeedReadyLeafAsync), so they control it despite not being the target.
		var result = await port.StartWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = otherWorkerId });

		result.WorkedByUserId.Should().Be(otherWorkerId);
	}

	[Fact]
	public async Task An_ancestor_owner_can_start_work_for_another_worker_on_a_descendant_leaf()
	{
		var (rootId, jobManagerId, branchOwnerId) = await SeedTreeAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var branch = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Branch",
			OwnerUserId = branchOwnerId,
			Priority = Priority.Medium,
		});
		var descendantLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Descendant leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = descendantLeaf.Id });
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.startfor.ancestor", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		// branchOwnerId owns the branch, not the leaf itself, so this exercises the ancestor-owner walk.
		var result = await port.StartWorkAsync(
			new() { Context = ContextFor(branchOwnerId), JobNodeId = descendantLeaf.Id, WorkedByUserId = otherWorkerId });

		result.WorkedByUserId.Should().Be(otherWorkerId);
	}

	[Fact]
	public async Task A_non_controlling_worker_cannot_start_work_for_another_worker()
	{
		var (_, _, _, leafId) = await SeedReadyLeafAsync();
		var bystanderId = await SeedEmployeeAsync("Bystander", "bystander.startfor", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.startfor.bystander", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(bystanderId), JobNodeId = leafId, WorkedByUserId = otherWorkerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_read_only_operational_role_cannot_start_work_for_another_worker()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var costViewerId = await SeedEmployeeAsync("Cost Viewer", "cost.viewer.startfor", EmployeeRole.CostViewer);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(costViewerId), JobNodeId = leafId, WorkedByUserId = workerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Starting_work_for_a_disabled_target_worker_throws_an_invariant_violation()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var disabledWorkerId = await SeedEmployeeAsync("Disabled Worker", "disabled.worker.startfor", EmployeeRole.Worker);
		await SetEnabledAsync(disabledWorkerId, false);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leafId, WorkedByUserId = disabledWorkerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-target-not-eligible");
	}

	[Fact]
	public async Task Starting_work_for_a_target_with_no_eligible_workflow_role_throws_an_invariant_violation()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var requesterId = await SeedEmployeeAsync("Requester Only", "requester.only.startfor", EmployeeRole.Requester);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leafId, WorkedByUserId = requesterId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-target-not-eligible");
	}

	private async Task SetEnabledAsync(AppUserId appUserId, bool isEnabled)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE identity_user SET is_enabled = @isEnabled WHERE app_user_id = @appUserId;";
		AddParameter(command, "@isEnabled", isEnabled);
		AddParameter(command, "@appUserId", appUserId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IWorkSessionCommandPort CreateSessionPort(string connectionString);

	internal abstract ILeafWorkQueryPort CreateLeafWorkQueryPort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	/// <summary>
	///     Seeds a deployed schema, an administrator/root (additionally granted
	///     <see cref="EmployeeRole.JobManager" />), one <see cref="EmployeeRole.Worker" /> employee, and a
	///     leaf with <c>LeafWork</c> already attached and ready (no prerequisites).
	/// </summary>
	private async Task<(JobNodeId RootId, AppUserId JobManagerId, AppUserId WorkerId, JobNodeId LeafId)> SeedReadyLeafAsync()
	{
		var (rootId, jobManagerId, workerId) = await SeedTreeAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Do the thing",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });

		return (rootId, jobManagerId, workerId, leaf.Id);
	}

	/// <summary>
	///     Same seeding as <see cref="SeedReadyLeafAsync" />, but the leaf has no <c>LeafWork</c>
	///     attached -- the case <see cref="IWorkSessionCommandPort.StartWorkAsync" /> exists for.
	/// </summary>
	private async Task<(JobNodeId RootId, AppUserId JobManagerId, AppUserId WorkerId, JobNodeId LeafId)> SeedBareLeafAsync()
	{
		var (rootId, jobManagerId, workerId) = await SeedTreeAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Fresh leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});

		return (rootId, jobManagerId, workerId, leaf.Id);
	}

	private async Task<(JobNodeId RootId, AppUserId JobManagerId, AppUserId WorkerId)> SeedTreeAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace.startwork",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		await using (var connection = await OpenExistingConnectionAsync()) {
			await AssignRoleAsync(connection, result.AdministratorId, EmployeeRole.JobManager);
		}

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.startwork", EmployeeRole.Worker);

		return (result.RootJobNodeId, result.AdministratorId, workerId);
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

		await AssignRoleAsync(connection, appUserId, role);

		return appUserId;
	}

	private static async Task AssignRoleAsync(DbConnection connection, AppUserId appUserId, EmployeeRole role)
	{
		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();
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
