namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     ADR 0068's standing guard over both deletion paths. Deleting a <c>job_node</c> — one node
///     (<c>JobNodeDependentCascade</c>) or a whole subtree (<c>SubtreeDeletionCascade</c>) — succeeds
///     only if every row referencing it has already gone, and every such foreign key bar
///     <c>app_user.home_node_id</c> is <c>ON DELETE RESTRICT</c>. A table added to the schema with a
///     reference nobody taught the cascade about therefore does not fail loudly at review; it fails
///     later, in production, as the catch-all "this job node cannot be deleted because it has
///     dependent data", and only for the rows that happen to have a dependent — which is exactly how
///     <c>job_request</c> and <c>node_rate_override</c> reached a deployed database undetected.
///     <para>
///         These tests read the deployed schema's own catalogue rather than the SQL sources, and hold
///         it against the dispositions below. Adding a reference into <c>job_node</c>, or a
///         delete-blocking trigger anywhere in the deletion closure, fails here until it is given a
///         disposition and the matching behaviour is proven in
///         <c>JobNodeCommandPortContractTestsBase</c>. Asserted identically for both providers, since
///         a divergence between them is itself the defect.
///     </para>
/// </summary>
public abstract class JobNodeDependentTableCoverageTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	/// <summary>
	///     Every foreign key into <c>job_node</c>, and how the deletion paths discharge it. Keep in step
	///     with <c>JobNodeDependentCascade</c> and <c>SubtreeDeletionCascade</c>.
	/// </summary>
	private static readonly IReadOnlyDictionary<string, DependentDisposition> ExpectedJobNodeReferences =
		new Dictionary<string, DependentDisposition> {
			// The hierarchy itself: single-node deletion refuses a node with children, the subtree
			// cascade removes them deepest-first.
			["job_node.parent_id"] = DependentDisposition.Structural,
			["leaf_work.job_node_id"] = DependentDisposition.Cascaded,
			["job_prerequisite.from_id"] = DependentDisposition.Cascaded,
			["job_prerequisite.to_id"] = DependentDisposition.Cascaded,
			["node_rate_override.node_id"] = DependentDisposition.Cascaded,
			["job_request.job_node_id"] = DependentDisposition.Cascaded,
			// Configuration that outlives the node it points at: both paths refuse by name.
			["request_holding_area.job_node_id"] = DependentDisposition.Refused,
			// The one reference the database clears by itself.
			["app_user.home_node_id"] = DependentDisposition.ClearedByDatabase,
		}.AsReadOnly();

	/// <summary>
	///     Every trigger that can refuse a <c>DELETE</c> on a table the deletion paths touch, with the
	///     reason it does not block them. A new one here means a delete that used to work can now be
	///     refused at commit — the ADR 0034/0061 collision this test exists to prevent recurring.
	/// </summary>
	private static readonly IReadOnlyDictionary<string, string> ExpectedDeleteBlockingTriggers =
		new Dictionary<string, string> {
			["job_node.job_node_root_guard_on_delete"] =
				"Refuses only the permanent root, which both deletion paths reject before reaching the database.",
			["job_request_note.job_request_note_no_delete"] =
				"ADR 0068: refuses only while the parent job_request still exists, so the ON DELETE CASCADE from " +
				"job_request passes through it.",
		}.AsReadOnly();

	/// <summary>Tables a job-node deletion writes to, so a delete-blocking trigger on one can abort it.</summary>
	private static readonly IReadOnlyList<string> DeletionClosureTables = [
		"job_node", "leaf_work", "work_session", "node_rate_override", "job_request", "job_request_note",
		"job_prerequisite",
	];

	private readonly IDisposableTestDatabase database;

	protected JobNodeDependentTableCoverageTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Every_foreign_key_into_job_node_has_a_declared_deletion_disposition()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var references = await ReadForeignKeysTargetingAsync(connection, "job_node");

		references.Should().BeEquivalentTo(ExpectedJobNodeReferences.Keys);
	}

	/// <summary>
	///     The disposition table is only worth anything if <c>RESTRICT</c> really is the rule: were a
	///     reference silently created <c>ON DELETE CASCADE</c>, rows the manifest promised to account
	///     for would vanish unannounced instead.
	/// </summary>
	[Fact]
	public async Task Only_the_home_node_reference_into_job_node_is_cleared_by_the_database_itself()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var setNullReferences = await ReadForeignKeysTargetingAsync(connection, "job_node", "SET NULL");

		setNullReferences.Should().BeEquivalentTo(
			ExpectedJobNodeReferences
				.Where(entry => entry.Value == DependentDisposition.ClearedByDatabase)
				.Select(entry => entry.Key));
	}

	[Fact]
	public async Task No_undeclared_trigger_can_refuse_a_delete_inside_the_deletion_closure()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var triggers = await ReadDeleteTriggersAsync(connection, DeletionClosureTables);

		triggers.Should().BeEquivalentTo(ExpectedDeleteBlockingTriggers.Keys);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>
	///     Every <c>referencing_table.referencing_column</c> holding a foreign key to
	///     <paramref name="targetTable" />, optionally narrowed to one delete rule (<c>RESTRICT</c>,
	///     <c>SET NULL</c>, <c>CASCADE</c>, <c>NO ACTION</c>), read from the provider's own catalogue.
	/// </summary>
	protected abstract Task<IReadOnlyList<string>> ReadForeignKeysTargetingAsync(
		DbConnection connection, string targetTable, string? deleteRule = null);

	/// <summary>Every <c>table.trigger_name</c> that fires on <c>DELETE</c> for one of <paramref name="tables" />.</summary>
	protected abstract Task<IReadOnlyList<string>> ReadDeleteTriggersAsync(DbConnection connection, IReadOnlyList<string> tables);

	private async Task<DbConnection> OpenDeployedConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	/// <summary>How a deletion path discharges one foreign key into <c>job_node</c>.</summary>
	private enum DependentDisposition
	{
		/// <summary>The deletion removes the dependent rows itself, inside the same transaction.</summary>
		Cascaded,

		/// <summary>The deletion is refused by name while the dependent row exists.</summary>
		Refused,

		/// <summary>The hierarchy edge: refused for a single node, removed deepest-first for a subtree.</summary>
		Structural,

		/// <summary>The database nulls the reference on delete; no application code is involved.</summary>
		ClearedByDatabase,
	}
}
