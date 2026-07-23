namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Remediation plan §2.4: runtime raw SQL -- writes (<c>ExecuteSql*</c>) <em>and</em> reads
///     (<c>SqlQuery*</c>/<c>FromSql*</c>) -- is allowed only for exact reviewed provider mechanisms
///     (source-controlled stored functions, advisory locks, connection pragmas, and the §7.4-sanctioned
///     recursive hierarchy walks EF cannot express), never merely because another call somewhere in the
///     same file needed an escape hatch. Guarding reads as well as writes closes the gap that would let
///     an EF-expressible query slip in as a hand-written <c>SqlQuery&lt;T&gt;</c>.
/// </summary>
public sealed class InlineDmlArchitectureTests
{
	private sealed record AllowedRawSqlCall(
		string Path,
		string ContainingMember,
		string Method,
		string RequiredArgumentFragment);

	private static readonly AllowedRawSqlCall[] AllowedRawSqlCalls = [
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PostgreSqlInstallationBootstrapPort.cs"),
			"BootstrapAsync",
			"ExecuteSqlInterpolatedAsync",
			"pg_advisory_xact_lock"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PostgreSqlJobNodeCommandPort.cs"),
			"MoveAsync",
			"ExecuteSqlInterpolatedAsync",
			"move_job_node"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PostgreSqlJobNodeCommandPort.cs"),
			"AddPrerequisiteAsync",
			"ExecuteSqlInterpolatedAsync",
			"add_job_prerequisite"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PostgreSqlJobNodeCommandPort.cs"),
			"AddPrerequisitesAsync",
			"ExecuteSqlInterpolatedAsync",
			"add_job_prerequisite"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PostgreSqlJobRequestCommandPort.cs"),
			"MoveAsync",
			"ExecuteSqlInterpolatedAsync",
			"move_job_node"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PrerequisiteReadinessSerialization.cs"),
			"AcquireAsync",
			"ExecuteSqlInterpolatedAsync",
			"pg_advisory_xact_lock"),
		new(
			Path.Combine("src", "JobTrack.Persistence.Sqlite", "SqliteDbContextFactory.cs"),
			"CreateContext",
			"ExecuteSqlRaw",
			"ConfigureConnectionSql"),
		new(
			Path.Combine("src", "JobTrack.Persistence.Sqlite", "SqliteDbContextFactory.cs"),
			"CreateOpenContextAsync",
			"ExecuteSqlRawAsync",
			"ConfigureConnectionSql"),
	];

	private static readonly HashSet<string> RawSqlMethods = new(StringComparer.Ordinal) {
		"ExecuteSqlInterpolatedAsync",
		"ExecuteSqlRaw",
		"ExecuteSqlRawAsync",
	};

	/// <summary>
	///     Every reviewed raw-SQL <em>read</em>: the recursive <c>WITH RECURSIVE</c> hierarchy walks EF
	///     cannot express (impl plan §7.4's sanctioned exception) and the source-controlled PostgreSQL
	///     set-returning functions invoked through EF. <see cref="SqliteControlledLeafQuery" /> builds its
	///     recursive statement in a member-local variable, so its fragment anchors on that (<c>sql</c>)
	///     rather than on SQL text the call site does not contain inline.
	/// </summary>
	private static readonly AllowedRawSqlCall[] AllowedRawSqlReadCalls = [
		new(HierarchyQueries, "GetAncestorOwnerIdsAsync", "SqlQuery", "WITH RECURSIVE"),
		new(HierarchyQueries, "GetAncestorIdsAsync", "SqlQuery", "WITH RECURSIVE"),
		new(HierarchyQueries, "PrerequisiteWouldCreateCycleAsync", "SqlQuery", "WITH RECURSIVE"),
		new(HierarchyQueries, "GetAncestorChainAsync", "SqlQuery", "WITH RECURSIVE"),
		new(HierarchyQueries, "GetSubtreeAchievementsAsync", "SqlQuery", "WITH RECURSIVE"),
		new(HierarchyQueries, "GetRequesterSubtreeAsync", "SqlQuery", "WITH RECURSIVE"),
		new(HierarchyQueries, "GetBoundedSubtreeAsync", "SqlQuery", "WITH RECURSIVE"),
		new(
			Path.Combine("src", "JobTrack.Persistence.Sqlite", "SqliteControlledLeafQuery.cs"),
			"GetControlledLeafIdsAsync",
			"SqlQueryRaw",
			"sql"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PostgreSqlWorkSessionQueryPort.cs"),
			"GetManageCapabilitiesAsync",
			"SqlQuery",
			"job_node_controlled_leaf_ids"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PrerequisiteReadinessSerialization.cs"),
			"HasActiveDependentWorkAsync",
			"SqlQuery",
			"jobtrack_has_active_dependent_work"),
		new(
			Path.Combine("src", "JobTrack.Persistence.Sqlite", "PrerequisiteReadinessSerialization.cs"),
			"HasActiveDependentWorkAsync",
			"SqlQuery",
			"WITH RECURSIVE"),
		new(
			Path.Combine("src", "JobTrack.Persistence.PostgreSql", "PostgreSqlCostQueryPort.cs"),
			"LoadWorkerSessionsAsync",
			"SqlQuery",
			"worker_overlapping_sessions"),
	];

	private static readonly HashSet<string> RawSqlReadMethods = new(StringComparer.Ordinal) {
		"SqlQuery",
		"SqlQueryRaw",
		"FromSql",
		"FromSqlRaw",
		"FromSqlInterpolated",
	};

	private static string HierarchyQueries =>
		Path.Combine("src", "JobTrack.Persistence.Shared", "JobNodeHierarchyQueries.cs");

	[Fact]
	public void Every_runtime_raw_sql_write_is_an_exact_reviewed_provider_mechanism() =>
		AssertRawSqlCallsMatchInventory(RawSqlMethods, AllowedRawSqlCalls, "write");

	[Fact]
	public void Every_runtime_raw_sql_read_is_an_exact_reviewed_provider_mechanism() =>
		AssertRawSqlCallsMatchInventory(RawSqlReadMethods, AllowedRawSqlReadCalls, "read");

	private static void AssertRawSqlCallsMatchInventory(HashSet<string> methods, AllowedRawSqlCall[] inventory, string kind)
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		string[] persistenceDirectories = [
			Path.Combine(solutionRoot, "src", "JobTrack.Persistence.PostgreSql"),
			Path.Combine(solutionRoot, "src", "JobTrack.Persistence.Sqlite"),
			Path.Combine(solutionRoot, "src", "JobTrack.Persistence.Shared"),
		];

		var actualCalls = persistenceDirectories
			.SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
			.Where(path => !path.Contains(Path.Combine("obj", ""), StringComparison.Ordinal)
						   && !path.Contains(Path.Combine("bin", ""), StringComparison.Ordinal))
			.SelectMany(path => FindRawSqlCalls(solutionRoot, path, methods))
			.ToList();

		var violations = actualCalls
			.Where(call => !inventory.Any(allowed =>
				allowed.Path == call.Path
				&& allowed.ContainingMember == call.ContainingMember
				&& allowed.Method == call.Method
				&& call.Arguments.Contains(allowed.RequiredArgumentFragment, StringComparison.Ordinal)))
			.Select(call => $"{call.Path}: {call.ContainingMember}.{call.Method}({call.Arguments})")
			.ToList();

		violations.Should().BeEmpty(
			$"EF-expressible reads/writes must use LINQ/EF, while raw SQL {kind}s are limited to exact reviewed " +
			"stored-function, advisory-lock, connection-pragma, and recursive-hierarchy mechanisms");

		actualCalls.Should().HaveCount(
			inventory.Length,
			$"removing or adding a raw-SQL {kind} mechanism requires updating the explicit reviewed-call inventory");
	}

	private static IEnumerable<(string Path, string ContainingMember, string Method, string Arguments)> FindRawSqlCalls(
		string solutionRoot,
		string path,
		HashSet<string> methods)
	{
		var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
		var relativePath = Path.GetRelativePath(solutionRoot, path);

		return root.DescendantNodes()
			.OfType<InvocationExpressionSyntax>()
			.Select(invocation => (
				Invocation: invocation,
				Method: invocation.Expression switch {
					MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
					IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
					_ => string.Empty,
				}))
			.Where(item => methods.Contains(item.Method))
			.Select(item => (
				Path: relativePath,
				ContainingMember: item.Invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Identifier.ValueText
					?? string.Empty,
				item.Method,
				Arguments: item.Invocation.ArgumentList.ToString()));
	}
}
