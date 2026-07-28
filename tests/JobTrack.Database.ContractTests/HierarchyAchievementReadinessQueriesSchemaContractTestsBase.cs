namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-HIER-004/ACHIEVE-002/PREREQ-003 contract for schema slice
///     13a: canonical hierarchy traversal, recursively derived achievement,
///     and prerequisite readiness/diagnostic queries (impl plan §6.2 item 13,
///     §6.5, spec §4/§5.2/§6, spec_claude Appendix C.1/C.2), asserted
///     identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlHierarchyAchievementReadinessQueriesSchemaTests" />
///     and <see cref="SqliteHierarchyAchievementReadinessQueriesSchemaTests" />.
///     Overlap-candidate discovery and the full cost-input sweep are the
///     remaining two sub-slices of item 13 and are out of scope here.
/// </summary>
public abstract class HierarchyAchievementReadinessQueriesSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;
	private const short AchievementWaiting = 1;
	protected const short AchievementSuccess = 3;

	private readonly IDisposableTestDatabase database;

	protected HierarchyAchievementReadinessQueriesSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_leaf_with_successful_leaf_work_has_succeeded()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var leafId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, leafId, AchievementSuccess);

		(await NodeSucceededAsync(connection, leafId)).Should().BeTrue();
	}

	[Fact]
	public async Task A_leaf_with_non_success_leaf_work_has_not_succeeded()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var leafId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, leafId, AchievementWaiting);

		(await NodeSucceededAsync(connection, leafId)).Should().BeFalse();
	}

	[Fact]
	public async Task A_leaf_without_leaf_work_has_not_succeeded()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var leafId = await InsertNodeAsync(connection, userId, rootId);

		(await NodeSucceededAsync(connection, leafId)).Should().BeFalse();
	}

	[Fact]
	public async Task An_empty_branch_with_no_children_and_no_leaf_work_has_not_succeeded()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var branchId = await InsertNodeAsync(connection, userId, rootId);

		(await NodeSucceededAsync(connection, branchId)).Should().BeFalse();
	}

	[Fact]
	public async Task A_branch_succeeds_only_when_every_child_including_grandchildren_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var branchId = await InsertNodeAsync(connection, userId, rootId);
		var childBranchId = await InsertNodeAsync(connection, userId, branchId);
		var leafOneId = await InsertNodeAsync(connection, userId, branchId);
		var leafTwoId = await InsertNodeAsync(connection, userId, childBranchId);
		await InsertLeafWorkAsync(connection, leafOneId, AchievementSuccess);
		await InsertLeafWorkAsync(connection, leafTwoId, AchievementSuccess);

		(await NodeSucceededAsync(connection, branchId)).Should().BeTrue();
	}

	[Fact]
	public async Task A_branch_does_not_succeed_when_one_descendant_leaf_fails_to_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var branchId = await InsertNodeAsync(connection, userId, rootId);
		var childBranchId = await InsertNodeAsync(connection, userId, branchId);
		var leafOneId = await InsertNodeAsync(connection, userId, branchId);
		var leafTwoId = await InsertNodeAsync(connection, userId, childBranchId);
		await InsertLeafWorkAsync(connection, leafOneId, AchievementSuccess);
		await InsertLeafWorkAsync(connection, leafTwoId, AchievementWaiting);

		(await NodeSucceededAsync(connection, branchId)).Should().BeFalse();
	}

	[Fact]
	public async Task Job_node_ancestors_returns_the_strict_chain_up_to_the_root_excluding_self()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var branchId = await InsertNodeAsync(connection, userId, rootId);
		var leafId = await InsertNodeAsync(connection, userId, branchId);

		(await AncestorsAsync(connection, leafId)).Should().BeEquivalentTo([branchId, rootId]);
	}

	[Fact]
	public async Task Job_node_descendants_returns_every_transitive_descendant_excluding_self()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var branchId = await InsertNodeAsync(connection, userId, rootId);
		var childBranchId = await InsertNodeAsync(connection, userId, branchId);
		var leafId = await InsertNodeAsync(connection, userId, childBranchId);
		var unrelatedId = await InsertNodeAsync(connection, userId, rootId);

		(await DescendantsAsync(connection, branchId)).Should().BeEquivalentTo([childBranchId, leafId]).And.NotContain(unrelatedId);
	}

	[Fact]
	public async Task Controlled_leaf_ids_returns_only_requested_leaves_owned_by_the_actor_or_an_ancestor()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (actorId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var (otherUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		var otherRootId = await InsertNodeAsync(connection, otherUserId, null);
		var actorOwnedBranchId = await InsertNodeAsync(connection, actorId, otherRootId);
		var leafControlledByBranchId = await InsertNodeAsync(connection, otherUserId, actorOwnedBranchId);
		var actorOwnedLeafId = await InsertNodeAsync(connection, actorId, otherRootId);
		var uncontrolledLeafId = await InsertNodeAsync(connection, otherUserId, otherRootId);

		var controlledIds = await ControlledLeafIdsAsync(
			connection, actorId, [leafControlledByBranchId, actorOwnedLeafId, uncontrolledLeafId]);

		controlledIds.Should().BeEquivalentTo([leafControlledByBranchId, actorOwnedLeafId]);
	}

	[Fact]
	public async Task A_node_with_no_prerequisites_is_ready()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var leafId = await InsertNodeAsync(connection, userId, rootId);

		(await NodeReadyAsync(connection, leafId)).Should().BeTrue();
	}

	[Fact]
	public async Task A_node_with_a_directly_attached_unsatisfied_prerequisite_is_not_ready()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredId = await InsertNodeAsync(connection, userId, rootId);
		var dependentId = await InsertNodeAsync(connection, userId, rootId);
		await AddPrerequisiteAsync(connection, requiredId, dependentId);

		(await NodeReadyAsync(connection, dependentId)).Should().BeFalse();
		var unsatisfied = await UnsatisfiedPrerequisitesAsync(connection, dependentId);
		unsatisfied.Should().ContainSingle(edge => edge.DeclaredAtNodeId == dependentId && edge.RequiredJobId == requiredId);
	}

	[Fact]
	public async Task A_node_with_an_inherited_unsatisfied_prerequisite_is_not_ready_and_identifies_the_declaring_ancestor()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredId = await InsertNodeAsync(connection, userId, rootId);
		var branchId = await InsertNodeAsync(connection, userId, rootId);
		var leafId = await InsertNodeAsync(connection, userId, branchId);
		await AddPrerequisiteAsync(connection, requiredId, branchId);

		(await NodeReadyAsync(connection, leafId)).Should().BeFalse();
		var unsatisfied = await UnsatisfiedPrerequisitesAsync(connection, leafId);
		unsatisfied.Should().ContainSingle(edge => edge.DeclaredAtNodeId == branchId && edge.RequiredJobId == requiredId);
	}

	[Fact]
	public async Task A_node_becomes_ready_once_its_prerequisite_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredId = await InsertNodeAsync(connection, userId, rootId);
		var dependentId = await InsertNodeAsync(connection, userId, rootId);
		await AddPrerequisiteAsync(connection, requiredId, dependentId);
		(await NodeReadyAsync(connection, dependentId)).Should().BeFalse();

		await InsertLeafWorkAsync(connection, requiredId, AchievementSuccess);

		(await NodeReadyAsync(connection, dependentId)).Should().BeTrue();
		(await UnsatisfiedPrerequisitesAsync(connection, dependentId)).Should().BeEmpty();
	}

	/// <summary>
	///     §2.2 of the 2026-07-28 fresh-eyes review: <c>job_node_blocked</c> must resolve each distinct
	///     required job's recursive achievement once, not once per prerequisite edge into it, and must
	///     still return exactly the same dependent set as before once every edge sharing a required job
	///     collapses onto that single evaluation.
	/// </summary>
	[Fact]
	public async Task Job_node_blocked_includes_the_dependent_of_an_unsatisfied_required_branch()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredBranchId = await InsertNodeAsync(connection, userId, rootId);
		var requiredLeafId = await InsertNodeAsync(connection, userId, requiredBranchId);
		await InsertLeafWorkAsync(connection, requiredLeafId, AchievementWaiting);
		var dependentId = await InsertNodeAsync(connection, userId, rootId);
		var unrelatedId = await InsertNodeAsync(connection, userId, rootId);
		await AddPrerequisiteAsync(connection, requiredBranchId, dependentId);

		var blocked = await BlockedNodeIdsAsync(connection);

		blocked.Should().Contain(dependentId).And.NotContain(unrelatedId).And.NotContain(requiredBranchId);
	}

	[Fact]
	public async Task Job_node_blocked_descends_to_every_child_of_a_blocked_dependent()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, requiredId, AchievementWaiting);
		var dependentBranchId = await InsertNodeAsync(connection, userId, rootId);
		await AddPrerequisiteAsync(connection, requiredId, dependentBranchId);
		var dependentLeafId = await InsertNodeAsync(connection, userId, dependentBranchId);

		var blocked = await BlockedNodeIdsAsync(connection);

		blocked.Should().Contain([dependentBranchId, dependentLeafId]);
	}

	[Fact]
	public async Task Job_node_blocked_excludes_dependents_of_a_satisfied_required_job()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, requiredId, AchievementSuccess);
		var dependentId = await InsertNodeAsync(connection, userId, rootId);
		await AddPrerequisiteAsync(connection, requiredId, dependentId);

		(await BlockedNodeIdsAsync(connection)).Should().NotContain(dependentId);
	}

	/// <summary>
	///     A dependent reachable both by its own direct, unsatisfied prerequisite and by descending from
	///     its blocked parent must still appear exactly once -- the duplicate-path convergence the
	///     original per-edge query's <c>UNION</c> already collapsed, and the distinct-required rewrite
	///     must preserve.
	/// </summary>
	[Fact]
	public async Task Job_node_blocked_returns_each_id_once_even_when_reachable_through_multiple_paths()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredBranchId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, await InsertNodeAsync(connection, userId, requiredBranchId), AchievementWaiting);
		var requiredLeafId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, requiredLeafId, AchievementWaiting);
		var dependentBranchId = await InsertNodeAsync(connection, userId, rootId);
		await AddPrerequisiteAsync(connection, requiredBranchId, dependentBranchId);
		var dependentLeafId = await InsertNodeAsync(connection, userId, dependentBranchId);
		await AddPrerequisiteAsync(connection, requiredLeafId, dependentLeafId);

		var blocked = await BlockedNodeIdsAsync(connection);

		blocked.Count(id => id == dependentLeafId).Should().Be(1);
	}

	/// <summary>Functional counterpart to the fan-out performance fixture: many dependents sharing one required branch.</summary>
	[Fact]
	public async Task Job_node_blocked_resolves_many_dependents_sharing_one_required_branch()
	{
		const int DependentCount = 25;
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, userId, null);
		var requiredBranchId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, await InsertNodeAsync(connection, userId, requiredBranchId), AchievementWaiting);

		var dependentIds = new List<long>();
		for (var i = 0; i < DependentCount; ++i) {
			var dependentId = await InsertNodeAsync(connection, userId, rootId);
			await AddPrerequisiteAsync(connection, requiredBranchId, dependentId);
			dependentIds.Add(dependentId);
		}

		(await BlockedNodeIdsAsync(connection)).Should().Contain(dependentIds);
	}

	/// <summary>PostgreSQL invokes the <c>job_node_blocked</c> stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<IReadOnlyList<long>> BlockedNodeIdsAsync(DbConnection connection);

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>PostgreSQL invokes the <c>node_succeeded</c> stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<bool> NodeSucceededAsync(DbConnection connection, long nodeId);

	/// <summary>PostgreSQL invokes the <c>job_node_ancestors</c> stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<IReadOnlyList<long>> AncestorsAsync(DbConnection connection, long nodeId);

	/// <summary>PostgreSQL invokes the <c>job_node_descendants</c> stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<IReadOnlyList<long>> DescendantsAsync(DbConnection connection, long nodeId);

	/// <summary>PostgreSQL invokes the canonical stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<IReadOnlyList<long>> ControlledLeafIdsAsync(
		DbConnection connection, long actorId, IReadOnlyList<long> leafIds);

	/// <summary>PostgreSQL invokes the <c>job_node_ready</c> stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<bool> NodeReadyAsync(DbConnection connection, long nodeId);

	/// <summary>PostgreSQL invokes the <c>job_node_unsatisfied_prerequisites</c> stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<IReadOnlyList<UnsatisfiedPrerequisite>> UnsatisfiedPrerequisitesAsync(DbConnection connection, long nodeId);

	private async Task<DbConnection> OpenDeployedConnectionAsync()
	{
		var connection = await OpenExistingConnectionAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static async Task<(long AppUserId, long IdentityUserId)> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		return (appUserId, 0);
	}

	private async Task<long> InsertNodeAsync(DbConnection connection, long ownerUserId, long? parentId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @ownerUserId, @ownerUserId, @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", "A job");
		AddParameter(command, "@ownerUserId", ownerUserId);
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task InsertLeafWorkAsync(DbConnection connection, long jobNodeId, short achievementId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO leaf_work (job_node_id, achievement_id, changed_at)
							  VALUES (@jobNodeId, @achievementId, @changedAt);
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@achievementId", achievementId);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task AddPrerequisiteAsync(DbConnection connection, long fromId, long toId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_prerequisite (from_id, to_id)
							  VALUES (@fromId, @toId);
							  """;
		AddParameter(command, "@fromId", fromId);
		AddParameter(command, "@toId", toId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}

/// <summary>One unsatisfied prerequisite edge, tagged with the node at which the inherited prerequisite was declared (spec §6).</summary>
public readonly record struct UnsatisfiedPrerequisite(long DeclaredAtNodeId, long RequiredJobId);
