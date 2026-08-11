namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Shared contract for <see cref="IReadinessQueryPort" />'s batch
///     <see cref="IReadinessQueryPort.GetReadinessInputsForNodesAsync" /> form, asserted identically
///     against PostgreSQL and SQLite by one thin sealed subclass per provider's own test project --
///     same shape as <see cref="AwaitingProgressQueryPortContractTestsBase" />. Regression coverage
///     for the bug fixed alongside this file: <c>JobQueries.GetJobSubtreeCoreAsync</c> evaluates
///     readiness for every row of a displayed subtree, and those rows are not all on one another's
///     ancestor chain (siblings, cousins) -- reusing the single-node
///     <see cref="IReadinessQueryPort.GetReadinessInputsAsync" /> result across every row threw
///     <c>InvariantViolationException</c> ("hierarchy.missing-node") for any row that was not an
///     ancestor of whichever single node the reused result happened to be scoped to.
/// </summary>
public abstract class ReadinessQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected ReadinessQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Includes_every_requested_node_even_when_they_are_siblings_not_on_one_anothers_ancestor_chain()
	{
		var tree = await SeedScenarioAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.GetReadinessInputsForNodesAsync([tree.LeafAId, tree.LeafBId]);

		result.NodesById.Keys.Should().Contain([tree.LeafAId, tree.LeafBId, tree.BranchId]);
		ReadinessCalculator.IsReady(tree.LeafAId, result.NodesById, result.Prerequisites).IsReady.Should().BeTrue();
		ReadinessCalculator.IsReady(tree.LeafBId, result.NodesById, result.Prerequisites).IsReady.Should().BeTrue();
	}

	[Fact]
	public async Task A_prerequisite_declared_on_a_shared_ancestor_blocks_every_descendant_requested_in_the_same_batch()
	{
		var tree = await SeedScenarioAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(tree.AdministratorId),
			RequiredJobId = tree.RequiredLeafId,
			DependentJobId = tree.BranchId,
		});

		var port = CreatePort(database.ConnectionString);
		var result = await port.GetReadinessInputsForNodesAsync([tree.LeafAId, tree.LeafBId]);

		ReadinessCalculator.IsReady(tree.LeafAId, result.NodesById, result.Prerequisites).IsReady.Should().BeFalse();
		ReadinessCalculator.IsReady(tree.LeafBId, result.NodesById, result.Prerequisites).IsReady.Should().BeFalse();
	}

	/// <summary>
	///     The <c>building-a-house.json</c> shape: within one branch, "Site survey" -&gt; "Excavate
	///     foundations" -&gt; "Pour foundations", the first two closed as Success. Browse requests the whole
	///     branch in one batch, so each required job is itself a requested row whose ancestor-chain stub
	///     carries no achievement -- treating that stub as loaded reported every dependent as blocked,
	///     against a satisfied prerequisite (the red stop palm must mean blocked, not merely "has a
	///     prerequisite").
	/// </summary>
	[Fact]
	public async Task A_satisfied_prerequisite_on_a_sibling_requested_in_the_same_batch_does_not_block()
	{
		var tree = await SeedScenarioAsync();
		var context = ContextFor(tree.AdministratorId);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var achievementPort = CreateAchievementPort(database.ConnectionString);
		await AddPrerequisiteAsync(jobNodePort, context, tree.LeafAId, tree.LeafBId);
		await FinishAsSuccessAsync(achievementPort, context, tree.LeafAId);

		var port = CreatePort(database.ConnectionString);
		var result = await port.GetReadinessInputsForNodesAsync([tree.BranchId, tree.LeafAId, tree.LeafBId]);

		ReadinessCalculator.IsReady(tree.LeafBId, result.NodesById, result.Prerequisites).IsReady.Should().BeTrue();
	}

	/// <summary>
	///     The same batch shape as the test above, with the required job left unfinished: the dependent
	///     is genuinely blocked and must still be reported so.
	/// </summary>
	[Fact]
	public async Task An_unsatisfied_prerequisite_on_a_sibling_requested_in_the_same_batch_still_blocks()
	{
		var tree = await SeedScenarioAsync();
		var context = ContextFor(tree.AdministratorId);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		await AddPrerequisiteAsync(jobNodePort, context, tree.LeafAId, tree.LeafBId);

		var port = CreatePort(database.ConnectionString);
		var result = await port.GetReadinessInputsForNodesAsync([tree.BranchId, tree.LeafAId, tree.LeafBId]);

		ReadinessCalculator.IsReady(tree.LeafBId, result.NodesById, result.Prerequisites).IsReady.Should().BeFalse();
	}

	/// <summary>
	///     A satisfied prerequisite chain across the batch: the middle node is both a dependent of a
	///     satisfied required job and the required job of the last, so the whole chain must come back
	///     unblocked once the first two succeed.
	/// </summary>
	[Fact]
	public async Task A_chain_of_satisfied_prerequisites_within_one_batch_leaves_every_link_unblocked()
	{
		var tree = await SeedScenarioAsync();
		var context = ContextFor(tree.AdministratorId);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var achievementPort = CreateAchievementPort(database.ConnectionString);
		await AddPrerequisiteAsync(jobNodePort, context, tree.LeafAId, tree.LeafBId);
		await AddPrerequisiteAsync(jobNodePort, context, tree.LeafBId, tree.RequiredLeafId);
		await FinishAsSuccessAsync(achievementPort, context, tree.LeafAId);
		await FinishAsSuccessAsync(achievementPort, context, tree.LeafBId);

		var port = CreatePort(database.ConnectionString);
		var result = await port.GetReadinessInputsForNodesAsync(
			[tree.BranchId, tree.LeafAId, tree.LeafBId, tree.RequiredLeafId]);

		ReadinessCalculator.IsReady(tree.LeafBId, result.NodesById, result.Prerequisites).IsReady.Should().BeTrue();
		ReadinessCalculator.IsReady(tree.RequiredLeafId, result.NodesById, result.Prerequisites).IsReady.Should().BeTrue();
	}

	/// <summary>
	///     <c>building-a-house.json</c>'s other prerequisite shape: "Structure" requires the whole
	///     "Groundworks" <em>branch</em>. Browsing the root batches the branch, its leaves, and the
	///     dependent together, so the required job arrives as an ancestor-chain stub of its own
	///     children — the case where the required job's derived (recursive) achievement, not a leaf
	///     achievement, decides the answer.
	/// </summary>
	[Fact]
	public async Task A_branch_prerequisite_is_satisfied_only_once_every_leaf_beneath_it_succeeds()
	{
		var tree = await SeedScenarioAsync();
		var context = ContextFor(tree.AdministratorId);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var achievementPort = CreateAchievementPort(database.ConnectionString);
		await AddPrerequisiteAsync(jobNodePort, context, tree.BranchId, tree.RequiredLeafId);
		await FinishAsSuccessAsync(achievementPort, context, tree.LeafAId);
		JobNodeId[] batch = [tree.BranchId, tree.LeafAId, tree.LeafBId, tree.RequiredLeafId];

		var port = CreatePort(database.ConnectionString);
		var whileLeafBUnfinished = await port.GetReadinessInputsForNodesAsync(batch);

		ReadinessCalculator.IsReady(tree.RequiredLeafId, whileLeafBUnfinished.NodesById, whileLeafBUnfinished.Prerequisites)
			.IsReady.Should().BeFalse("Leaf B under the required branch has not succeeded yet");

		await FinishAsSuccessAsync(achievementPort, context, tree.LeafBId);
		var afterLeafBSucceeds = await port.GetReadinessInputsForNodesAsync(batch);

		ReadinessCalculator.IsReady(tree.RequiredLeafId, afterLeafBSucceeds.NodesById, afterLeafBSucceeds.Prerequisites)
			.IsReady.Should().BeTrue("every leaf beneath the required branch has succeeded");
	}

	[Fact]
	public async Task Batch_query_count_is_constant_as_distinct_required_jobs_increase()
	{
		var tree = await SeedScenarioAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var secondRequiredLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(tree.AdministratorId),
			ParentId = tree.RootId,
			Description = "Second required leaf",
			OwnerUserId = tree.AdministratorId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(tree.AdministratorId), JobNodeId = secondRequiredLeaf.Id });
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(tree.AdministratorId),
			RequiredJobId = tree.RequiredLeafId,
			DependentJobId = tree.LeafAId,
		});
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(tree.AdministratorId),
			RequiredJobId = secondRequiredLeaf.Id,
			DependentJobId = tree.LeafBId,
		});

		var narrowCommands = new CommandCountInterceptor();
		var narrowPort = CreatePort(database.ConnectionString, [narrowCommands]);
		_ = await narrowPort.GetReadinessInputsForNodesAsync([tree.LeafAId]);

		var wideCommands = new CommandCountInterceptor();
		var transactions = new TransactionCountInterceptor();
		var widePort = CreatePort(database.ConnectionString, [wideCommands, transactions]);
		_ = await widePort.GetReadinessInputsForNodesAsync([tree.LeafAId, tree.LeafBId]);

		wideCommands.Count.Should().Be(narrowCommands.Count);
		transactions.Count.Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	internal abstract IReadinessQueryPort CreatePort(string connectionString);

	internal abstract IReadinessQueryPort CreatePort(string connectionString, IReadOnlyList<IInterceptor> interceptors);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static Task AddPrerequisiteAsync(
		IJobNodeCommandPort jobNodePort, CommandContext context, JobNodeId requiredJobId, JobNodeId dependentJobId) =>
		jobNodePort.AddPrerequisiteAsync(new() { Context = context, RequiredJobId = requiredJobId, DependentJobId = dependentJobId });

	/// <summary>
	///     Drives an already-attached leaf through the real achievement command port to
	///     <see cref="Achievement.Success" /> -- the seeded leaves start at version 1, Waiting.
	/// </summary>
	private static async Task FinishAsSuccessAsync(
		IAchievementCommandPort achievementPort, CommandContext context, JobNodeId nodeId)
	{
		const long attachedVersion = 1;
		var inProgress = await achievementPort.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = nodeId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = attachedVersion,
		});
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = nodeId,
			NewAchievement = Achievement.Success,
			Reason = "Done",
			Version = inProgress.Version,
		});
	}

	/// <summary>
	///     Seeds root (administrator-owned) -&gt; branch, with two sibling leaves (LeafA, LeafB) and an
	///     unrelated Waiting leaf (RequiredLeaf) available to be declared as a prerequisite.
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
			UserName = "ada.lovelace.readiness",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;
		var context = ContextFor(administratorId);

		var jobNodePort = CreateJobNodePort(database.ConnectionString);

		var branch = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = bootstrap.RootJobNodeId,
			Description = "Branch",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var leafA = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = branch.Id,
			Description = "Leaf A",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = context, JobNodeId = leafA.Id });
		var leafB = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = branch.Id,
			Description = "Leaf B",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = context, JobNodeId = leafB.Id });

		var requiredLeaf = await jobNodePort.AddChildAsync(new() {
			Context = context,
			ParentId = bootstrap.RootJobNodeId,
			Description = "Required leaf, elsewhere in the tree",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = context, JobNodeId = requiredLeaf.Id });

		return new(administratorId, bootstrap.RootJobNodeId, branch.Id, leafA.Id, leafB.Id, requiredLeaf.Id);
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private sealed record SeededTree(
		AppUserId AdministratorId,
		JobNodeId RootId,
		JobNodeId BranchId,
		JobNodeId LeafAId,
		JobNodeId LeafBId,
		JobNodeId RequiredLeafId);
}
