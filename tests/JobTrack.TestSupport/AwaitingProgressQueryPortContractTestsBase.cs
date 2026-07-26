namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter());
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Select(e => e.Id).Should().BeEquivalentTo([
			tree.WaitingLeafId, tree.InProgressLeafId, tree.RequiredLeafId, tree.UnassignedLeafId, tree.NoLeafWorkLeafId, tree.BlockedLeafId,
			tree.OutsideBranchLeafId,
		]);
	}

	[Fact]
	public async Task Includes_a_leaf_with_no_LeafWork_attached_with_a_null_achievement()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter());

		result.NodesById[tree.NoLeafWorkLeafId].LeafAchievement.Should().BeNull();
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);
		entries.Single(e => e.Id == tree.NoLeafWorkLeafId).Achievement.Should().BeNull();
	}

	[Fact]
	public async Task Excludes_an_archived_leaf()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter());
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Select(e => e.Id).Should().NotContain(tree.ArchivedLeafId);
	}

	[Fact]
	public async Task Keeps_a_leaf_blocked_by_an_unsatisfied_prerequisite_on_the_list_marked_not_ready()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter());
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Single(e => e.Id == tree.BlockedLeafId).IsReady.Should().BeFalse();
	}

	[Fact]
	public async Task Carries_owner_priority_and_deadline_facts_through()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter());

		var facts = result.FactsById[tree.WaitingLeafId];
		facts.OwnerUserId.Should().Be(tree.WorkerId);
		facts.Priority.Should().Be(Priority.High);
	}

	[Fact]
	public async Task Carries_null_owner_facts_through_for_unassigned_leaves()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter());

		var facts = result.FactsById[tree.UnassignedLeafId];
		facts.OwnerUserId.Should().BeNull();
	}

	/// <summary>2026-07-25 scalability-follow-up plan §2.1: ownership is the port's own query now.</summary>
	[Fact]
	public async Task An_owner_filter_restricts_the_query_to_that_owners_leaves()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { Ownership = OwnershipFilter.OwnedBy(tree.WorkerId) });
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Select(e => e.Id).Should().BeEquivalentTo([tree.WaitingLeafId]);
	}

	[Fact]
	public async Task An_unassigned_filter_restricts_the_query_to_leaves_with_no_owner()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { Ownership = OwnershipFilter.Unassigned });
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Select(e => e.Id).Should().BeEquivalentTo([tree.UnassignedLeafId]);
	}

	[Fact]
	public async Task A_search_text_filter_restricts_the_query_to_matching_descriptions()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { SearchText = "cabinets" });
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Select(e => e.Id).Should().BeEquivalentTo([tree.WaitingLeafId]);
	}

	[Fact]
	public async Task Search_is_ordinal_ignore_case_for_non_ascii_text()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { SearchText = "ångström" });
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Select(e => e.Id).Should().BeEquivalentTo([tree.WaitingLeafId]);
	}

	[Fact]
	public async Task A_subtree_filter_restricts_the_query_to_descendants_of_the_scope_root()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { SubtreeRootId = tree.BranchId });
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Select(e => e.Id).Should().BeEquivalentTo([
			tree.WaitingLeafId, tree.InProgressLeafId, tree.RequiredLeafId, tree.UnassignedLeafId, tree.NoLeafWorkLeafId, tree.BlockedLeafId,
		]);
	}

	[Fact]
	public async Task A_subtree_filter_with_no_unfinished_descendants_returns_an_empty_result()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { SubtreeRootId = tree.SuccessLeafId });
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Should().BeEmpty();
	}

	[Fact]
	public async Task A_subtree_filter_is_composed_into_candidate_selection_without_an_extra_id_materialization_command()
	{
		var tree = await SeedScenarioAsync();

		var unfilteredCommands = new CommandCountInterceptor();
		var unfilteredPort = CreatePort(database.ConnectionString, [unfilteredCommands]);
		_ = await unfilteredPort.GetAwaitingProgressInputsAsync(DefaultFilter());

		var subtreeCommands = new CommandCountInterceptor();
		var subtreePort = CreatePort(database.ConnectionString, [subtreeCommands]);
		_ = await subtreePort.GetAwaitingProgressInputsAsync(DefaultFilter() with { SubtreeRootId = tree.BranchId });

		subtreeCommands.Count.Should().Be(unfilteredCommands.Count);
	}

	/// <summary>
	///     2026-07-25 scalability-follow-up plan §2.1: ordering and paging are the port's own query
	///     now -- consecutive pages over the exact descending-priority/ascending-deadline-nulls-last/
	///     ascending-id ordering must not gap or overlap.
	/// </summary>
	[Fact]
	public async Task Offset_and_limit_page_the_ordered_result_without_gaps_or_overlap()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var unpaged = await port.GetAwaitingProgressInputsAsync(DefaultFilter());
		var unpagedEntries = AwaitingProgressCalculator.GetAwaitingProgress(unpaged.NodesById, unpaged.FactsById, unpaged.Prerequisites);

		var firstPageResult = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { Offset = 0, Limit = 3 });
		var firstPage = AwaitingProgressCalculator.GetAwaitingProgress(
			firstPageResult.NodesById, firstPageResult.FactsById, firstPageResult.Prerequisites);
		var secondPageResult = await port.GetAwaitingProgressInputsAsync(DefaultFilter() with { Offset = 3, Limit = 4 });
		var secondPage = AwaitingProgressCalculator.GetAwaitingProgress(
			secondPageResult.NodesById, secondPageResult.FactsById, secondPageResult.Prerequisites);

		firstPage.Should().HaveCount(3);
		secondPage.Select(e => e.Id).Should().BeEquivalentTo(unpagedEntries.Skip(3).Select(e => e.Id));
		firstPage.Select(e => e.Id).Concat(secondPage.Select(e => e.Id)).Should().BeEquivalentTo(unpagedEntries.Select(e => e.Id));
	}

	/// <summary>
	///     2026-07-24 code-review-scalability-remediation-plan §2.2 step 4: the port must load only
	///     currently-unfinished leaves (plus the ancestor/required-job facts readiness needs), never the
	///     whole <c>job_node</c> table. A finished, otherwise-unrelated decoy subtree proves the
	///     narrowing; a cross-branch prerequisite whose required job is itself finished (so it is not an
	///     unfinished-leaf candidate) proves the required-job achievement is still resolved correctly
	///     through the narrowed load, not just omitted.
	/// </summary>
	[Fact]
	public async Task Excludes_a_large_unrelated_finished_subtree_while_a_cross_branch_prerequisite_still_resolves_correctly()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Grace Hopper",
			IanaTimeZone = "Europe/London",
			UserName = "grace.hopper.narrowing",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;
		var context = ContextFor(administratorId);

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var achievementPort = CreateAchievementPort(database.ConnectionString);

		var branchMain = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = bootstrap.RootJobNodeId,
			Description = "Main branch",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var candidateLeaf = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = branchMain.Id,
			Description = "Candidate leaf",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = context, JobNodeId = candidateLeaf.Id });

		var branchDecoy = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = bootstrap.RootJobNodeId,
			Description = "Decoy branch",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});

		var requiredLeaf = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = branchDecoy.Id,
			Description = "Required leaf, elsewhere in the tree",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		await FinishAsSuccessAsync(jobNodePort, achievementPort, context, requiredLeaf.Id);

		await jobNodePort.AddPrerequisiteAsync(new() { Context = context, RequiredJobId = requiredLeaf.Id, DependentJobId = candidateLeaf.Id });

		var decoyIds = new List<JobNodeId>();
		for (var index = 0; index < 30; ++index) {
			var decoy = await jobNodePort.AddChildAsync(new() {
				Context = context,
				ParentId = branchDecoy.Id,
				Description = $"Decoy {index}",
				OwnerUserId = administratorId,
				Priority = Priority.Medium,
			});
			await FinishAsSuccessAsync(jobNodePort, achievementPort, context, decoy.Id);
			decoyIds.Add(decoy.Id);
		}

		var port = CreatePort(database.ConnectionString);
		var result = await port.GetAwaitingProgressInputsAsync(DefaultFilter());
		var entries = AwaitingProgressCalculator.GetAwaitingProgress(result.NodesById, result.FactsById, result.Prerequisites);

		entries.Single(e => e.Id == candidateLeaf.Id).IsReady.Should().BeTrue();
		result.NodesById.Keys.Should().NotContain(decoyIds);
		result.NodesById.Keys.Should().NotContain(branchDecoy.Id);
	}

	/// <summary>A generous default page covering every leaf <see cref="SeedScenarioAsync" /> creates.</summary>
	private static AwaitingProgressQueryFilter DefaultFilter() => new() { Ownership = OwnershipFilter.All, Offset = 0, Limit = 100 };

	private static async Task FinishAsSuccessAsync(
		IJobNodeCommandPort jobNodePort, IAchievementCommandPort achievementPort, CommandContext context, JobNodeId nodeId)
	{
		var attached = await jobNodePort.AttachLeafWorkAsync(new() { Context = context, JobNodeId = nodeId });
		var inProgress = await achievementPort.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = nodeId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = attached.Version,
		});
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = nodeId,
			NewAchievement = Achievement.Success,
			Reason = "Done",
			Version = inProgress.Version,
		});
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	internal abstract IAwaitingProgressQueryPort CreatePort(string connectionString);

	internal abstract IAwaitingProgressQueryPort CreatePort(string connectionString, IReadOnlyList<IInterceptor> interceptors);

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
			Description = "Install Ångström cabinets",
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

		// Sibling of "Kitchen renovation", directly under root -- outside branch.Id's own subtree, so
		// A_subtree_filter_restricts_the_query_to_descendants_of_the_scope_root can prove the port's
		// subtree scoping actually excludes a leaf that would otherwise qualify.
		var outsideBranchLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Outside the branch",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = outsideBranchLeaf.Id });

		return new(
			jobManagerId, workerId, branch.Id, waitingLeaf.Id, inProgressLeaf.Id, successLeaf.Id, noLeafWorkLeaf.Id, archivedLeaf.Id,
			blockedLeaf.Id, requiredLeaf.Id, unassignedLeaf.Id, outsideBranchLeaf.Id);
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
		JobNodeId BranchId,
		JobNodeId WaitingLeafId,
		JobNodeId InProgressLeafId,
		JobNodeId SuccessLeafId,
		JobNodeId NoLeafWorkLeafId,
		JobNodeId ArchivedLeafId,
		JobNodeId BlockedLeafId,
		JobNodeId RequiredLeafId,
		JobNodeId UnassignedLeafId,
		JobNodeId OutsideBranchLeafId);
}
