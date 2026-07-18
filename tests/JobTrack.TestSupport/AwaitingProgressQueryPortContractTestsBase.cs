namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Hierarchy;

/// <summary>
///     Shared contract for <see cref="IAwaitingProgressQueryPort" />, asserted identically against
///     PostgreSQL and SQLite by one thin sealed subclass per provider's own test project — same shape
///     as <see cref="JobBrowseQueryPortContractTestsBase" />. Seeds a small tree via the real
///     <see cref="IInstallationBootstrapPort" />/<see cref="IJobNodeCommandPort" />/
///     <see cref="IAchievementCommandPort" />, not hand-rolled SQL, except for the second employee row
///     (no employee-creation port exists at this layer, so it's seeded the same way
///     <see cref="JobBrowseQueryPortContractTestsBase" /> seeds its worker).
/// </summary>
public abstract class AwaitingProgressQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected AwaitingProgressQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Returns_every_unfinished_leaf_including_one_with_no_LeafWork_attached_and_one_blocked()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync();
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(
			result.NodesById, result.FactsById, result.Prerequisites, OwnershipFilter.All, null);

		entries.Select(e => e.Id).Should().BeEquivalentTo([
			tree.WaitingLeafId, tree.InProgressLeafId, tree.RequiredLeafId, tree.UnassignedLeafId, tree.NoLeafWorkLeafId, tree.BlockedLeafId,
		]);
	}

	[Fact]
	public async Task Includes_a_leaf_with_no_LeafWork_attached_with_a_null_achievement()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync();

		result.NodesById[tree.NoLeafWorkLeafId].LeafAchievement.Should().BeNull();
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(
			result.NodesById, result.FactsById, result.Prerequisites, OwnershipFilter.All, null);
		entries.Single(e => e.Id == tree.NoLeafWorkLeafId).Achievement.Should().BeNull();
	}

	[Fact]
	public async Task Excludes_an_archived_leaf()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync();
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(
			result.NodesById, result.FactsById, result.Prerequisites, OwnershipFilter.All, null);

		entries.Select(e => e.Id).Should().NotContain(tree.ArchivedLeafId);
	}

	[Fact]
	public async Task Keeps_a_leaf_blocked_by_an_unsatisfied_prerequisite_on_the_list_marked_not_ready()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync();
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(
			result.NodesById, result.FactsById, result.Prerequisites, OwnershipFilter.All, null);

		entries.Single(e => e.Id == tree.BlockedLeafId).IsReady.Should().BeFalse();
	}

	[Fact]
	public async Task Carries_owner_priority_and_deadline_facts_through()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync();

		var facts = result.FactsById[tree.WaitingLeafId];
		facts.OwnerUserId.Should().Be(tree.WorkerId);
		facts.Priority.Should().Be(Priority.High);
	}

	[Fact]
	public async Task Carries_null_owner_facts_through_for_unassigned_leaves()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync();

		var facts = result.FactsById[tree.UnassignedLeafId];
		facts.OwnerUserId.Should().BeNull();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	protected abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	protected abstract IAwaitingProgressQueryPort CreatePort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	/// <summary>
	///     Seeds root (administrator-owned) -&gt; branch "Kitchen renovation", with: a worker-owned
	///     Waiting leaf (high priority), an administrator-owned InProgress leaf, a Success leaf, a leaf
	///     with no LeafWork attached, an archived Waiting leaf, and a required/dependent pair where the
	///     required leaf has not succeeded (leaving the dependent blocked).
	/// </summary>
	private async Task<SeededTree> SeedScenarioAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace.awaiting",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var jobManagerId = bootstrap.AdministratorId;

		await using (var connection = await OpenExistingConnectionAsync()) {
			await AssignRoleAsync(connection, jobManagerId, EmployeeRole.JobManager);
		}

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.awaiting", EmployeeRole.Worker);

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var achievementPort = CreateAchievementPort(database.ConnectionString);

		var branch = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Kitchen renovation",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});

		var waitingLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Install cabinets",
			OwnerUserId = workerId,
			Priority = Priority.High,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = waitingLeaf.Id });

		var inProgressLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Install plumbing",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});
		var inProgressLeafWork = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = inProgressLeaf.Id });
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = inProgressLeaf.Id,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = inProgressLeafWork.Version,
		});

		var unassignedLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = unassignedLeaf.Id });

		var successLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Finished painting",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});
		var successLeafWork = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = successLeaf.Id });
		var inProgressSuccessLeafWork = await achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = successLeaf.Id,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = successLeafWork.Version,
		});
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = successLeaf.Id,
			NewAchievement = Achievement.Success,
			Reason = "Done",
			Version = inProgressSuccessLeafWork.Version,
		});

		var noLeafWorkLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Not yet started",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});

		var archivedLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Old wiring job",
			OwnerUserId = jobManagerId,
			Priority = Priority.Low,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = archivedLeaf.Id });
		_ = await jobNodePort.ArchiveAsync(
			new() { Context = ContextFor(jobManagerId), NodeId = archivedLeaf.Id, Version = archivedLeaf.Version });

		var requiredLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Required leaf",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = requiredLeaf.Id });
		var blockedLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Blocked leaf",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = blockedLeaf.Id });
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = requiredLeaf.Id,
			DependentJobId = blockedLeaf.Id,
		});

		return new(
			jobManagerId, workerId, waitingLeaf.Id, inProgressLeaf.Id, successLeaf.Id, noLeafWorkLeaf.Id, archivedLeaf.Id, blockedLeaf.Id,
			requiredLeaf.Id, unassignedLeaf.Id);
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

	private sealed record SeededTree(
		AppUserId JobManagerId,
		AppUserId WorkerId,
		JobNodeId WaitingLeafId,
		JobNodeId InProgressLeafId,
		JobNodeId SuccessLeafId,
		JobNodeId NoLeafWorkLeafId,
		JobNodeId ArchivedLeafId,
		JobNodeId BlockedLeafId,
		JobNodeId RequiredLeafId,
		JobNodeId UnassignedLeafId);
}
