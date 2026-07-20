namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Remediation plan §2.5: ADR 0016 requires DI-registered Noda Time <c>IClock</c> as the sole
///     source of "now", captured once per operation. Everywhere outside the small set of composition
///     roots that own the default <c>IClock</c> binding, a direct <c>SystemClock.Instance</c> read or
///     <c>DateTimeOffset.UtcNow</c> read is a bug: it makes the call site untestable and lets an
///     operation observe two different "now" values across its own writes.
/// </summary>
public sealed class ClockCompositionArchitectureTests
{
	/// <summary>
	///     Files allowed to reference <c>SystemClock.Instance</c>/<c>DateTimeOffset.UtcNow</c> directly:
	///     the provider factories' <c>IClock? clock = null</c> default and each host's DI registration
	///     of the default clock. Every other runtime call site must take <c>IClock</c> as a dependency.
	/// </summary>
	private static readonly string[] CompositionRootAllowlist = [
		Path.Combine("src", "JobTrack.Persistence.PostgreSql", "JobTrackPostgreSql.cs"),
		Path.Combine("src", "JobTrack.Persistence.Sqlite", "JobTrackSqlite.cs"),
		Path.Combine("src", "JobTrack.Web", "Program.cs"),
		Path.Combine("src", "JobTrack.AdminCli", "Program.cs"),
		Path.Combine("src", "JobTrack.Database", "Program.cs"),
		Path.Combine("src", "JobTrack.Database", "SchemaDeployer.cs"),
		Path.Combine("src", "JobTrack.Identity", "ServiceCollectionExtensions.cs"),
	];

	private static readonly string[] DirectClockReadTokens = ["SystemClock.Instance", "DateTimeOffset.UtcNow"];

	[Fact]
	public void No_runtime_source_file_reads_the_wall_clock_directly_outside_the_composition_roots()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		var srcDirectory = Path.Combine(solutionRoot, "src");

		var violations = Directory.EnumerateFiles(srcDirectory, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.Combine("obj", ""), StringComparison.Ordinal)
						   && !path.Contains(Path.Combine("bin", ""), StringComparison.Ordinal))
			.Where(path => !CompositionRootAllowlist.Contains(Path.GetRelativePath(solutionRoot, path), StringComparer.Ordinal))
			.Where(path => DirectClockReadTokens.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
			.Select(path => Path.GetRelativePath(solutionRoot, path))
			.ToList();

		violations.Should().BeEmpty(
			"ADR 0016 requires DI-registered IClock as the sole source of \"now\" everywhere outside the " +
			"provider-factory/host composition roots that supply its default binding");
	}
}
