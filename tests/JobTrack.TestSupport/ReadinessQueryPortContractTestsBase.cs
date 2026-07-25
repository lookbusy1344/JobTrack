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

	internal abstract IReadinessQueryPort CreatePort(string connectionString);

	internal abstract IReadinessQueryPort CreatePort(string connectionString, IReadOnlyList<IInterceptor> interceptors);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

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
