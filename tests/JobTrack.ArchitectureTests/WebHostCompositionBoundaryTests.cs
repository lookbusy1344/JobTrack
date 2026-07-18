namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Plan §8.7 web gate: "the web project has no direct reference to provider implementation APIs
///     beyond composition registration and no SQL." <c>JobTrack.Web.csproj</c> legitimately project-
///     references both persistence providers (its composition root, <c>Program.cs</c>, calls
///     <c>JobTrackPostgreSql.Create</c>/<c>JobTrackSqlite.Create</c> and
///     <c>AddJobTrackIdentityPostgreSql</c>/<c>AddJobTrackIdentitySqlite</c>), so
///     <see cref="ReusableLibraryDependencyTests" />'s assembly-reference check can't express this --
///     the reference itself is allowed, only its use outside the composition root is not. This is a
///     source-text scan instead.
/// </summary>
public sealed class WebHostCompositionBoundaryTests
{
	private const string CompositionRootFileName = "Program.cs";

	private static readonly string[] ForbiddenProviderNamespaces = [
		"JobTrack.Persistence.PostgreSql",
		"JobTrack.Persistence.Sqlite",
	];

	private static readonly string[] SqlKeywordPatterns = [
		"SELECT ", "INSERT INTO", "UPDATE ", "DELETE FROM", "FromSqlRaw", "FromSql(", "ExecuteSqlRaw", "ExecuteSql(",
	];

	[Fact]
	public void No_file_outside_the_composition_root_references_a_persistence_provider_namespace()
	{
		var violations = new List<string>();

		foreach (var path in RazorPagesAndCSharpSourceFiles()) {
			if (Path.GetFileName(path) == CompositionRootFileName) {
				continue;
			}

			var content = File.ReadAllText(path);
			foreach (var forbiddenNamespace in ForbiddenProviderNamespaces) {
				if (content.Contains(forbiddenNamespace, StringComparison.Ordinal)) {
					violations.Add($"{Path.GetFileName(path)} references '{forbiddenNamespace}'");
				}
			}
		}

		violations.Should().BeEmpty(
			"only the composition root (Program.cs) may reference a persistence provider implementation directly");
	}

	[Fact]
	public void No_file_contains_direct_SQL()
	{
		var violations = new List<string>();

		foreach (var path in RazorPagesAndCSharpSourceFiles()) {
			var content = File.ReadAllText(path);
			foreach (var pattern in SqlKeywordPatterns) {
				// Ordinal (case-sensitive): SQL-cased keywords ("SELECT ", "UPDATE ") almost never
				// occur in prose/markup, unlike their lowercase or Title Case English-word forms
				// ("update achievement" in a page heading, "select" in a <select> element).
				if (content.Contains(pattern, StringComparison.Ordinal)) {
					violations.Add($"{Path.GetFileName(path)} contains '{pattern}'");
				}
			}
		}

		violations.Should().BeEmpty("JobTrack.Web must not embed SQL -- every read/write goes through IJobTrackClient");
	}

	[Fact]
	public void Identity_adapter_does_not_write_audit_events_with_direct_SQL()
	{
		var identityProjectDirectory = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Identity");
		var violations = Directory.EnumerateFiles(identityProjectDirectory, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
						   && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Where(path => File.ReadAllText(path).Contains("audit_event", StringComparison.Ordinal))
			.Select(path => Path.GetFileName(path))
			.ToList();

		violations.Should().BeEmpty(
			"authentication audit writes must go through the application client and persistence ports, not the Identity adapter");
	}

	private static IEnumerable<string> RazorPagesAndCSharpSourceFiles()
	{
		var webProjectDirectory = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web");

		return Directory.EnumerateFiles(webProjectDirectory, "*.cs", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(webProjectDirectory, "*.cshtml", SearchOption.AllDirectories))
			.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
						   && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
	}
}
