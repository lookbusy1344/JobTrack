namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Remediation plan §2.4: guards the civil-time boundary rule (repository CLAUDE.md) that a Razor
///     Page field representing an instant is a <c>string</c> parsed through <c>BackdateInstant</c> in
///     the viewer's own zone, never a bound <c>DateTimeOffset</c>/<c>Instant</c> resolved through the
///     server process's own zone.
/// </summary>
public sealed class WebHostCivilTimeArchitectureTests
{
	/// <summary>
	///     Files under <c>Pages</c> that render a schedule's <c>LocalDate</c> effective range (Rota),
	///     not an <c>Instant</c> -- <c>EffectiveStart</c>/<c>EffectiveEnd</c> is a legitimately overloaded
	///     name across the codebase; date-only schedule fields are explicitly out of scope for this rule.
	/// </summary>
	private static readonly string[] LocalDateEffectiveRangeAllowlist = [
		Path.Combine("Rota", "Index.cshtml"),
		Path.Combine("Rota", "CorrectVersion.cshtml"),
	];

	private static readonly string[] InstantPropertyNames = ["EffectiveStart", "EffectiveEnd", "Segment.Start", "Segment.End"];

	[Fact]
	public void No_Razor_Page_code_behind_binds_an_instant_through_DateTimeOffset()
	{
		var pagesDirectory = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "Pages");
		var violations = Directory.EnumerateFiles(pagesDirectory, "*.cshtml.cs", SearchOption.AllDirectories)
			.Where(path => File.ReadAllText(path).Contains("DateTimeOffset", StringComparison.Ordinal))
			.Select(path => Path.GetRelativePath(pagesDirectory, path))
			.ToList();

		violations.Should().BeEmpty(
			"a Razor Page field for a point in time is a string parsed through BackdateInstant in the viewer's own zone, " +
			"never a bound DateTimeOffset resolved through the server process's own zone");
	}

	[Fact]
	public void No_cshtml_page_renders_a_rate_or_cost_trace_instant_without_InstantDisplay()
	{
		var pagesDirectory = Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "Pages");
		var violations = new List<string>();

		foreach (var path in Directory.EnumerateFiles(pagesDirectory, "*.cshtml", SearchOption.AllDirectories)) {
			var relativePath = Path.GetRelativePath(pagesDirectory, path);
			if (LocalDateEffectiveRangeAllowlist.Contains(relativePath, StringComparer.Ordinal)) {
				continue;
			}

			var lineNumber = 0;
			foreach (var line in File.ReadLines(path)) {
				lineNumber++;
				if (!line.Contains('@', StringComparison.Ordinal)
					|| line.Contains("asp-for", StringComparison.Ordinal)
					|| line.Contains("asp-validation-for", StringComparison.Ordinal)
					|| line.Contains("InstantDisplay.Format", StringComparison.Ordinal)) {
					continue;
				}

				if (InstantPropertyNames.Any(name => line.Contains(name, StringComparison.Ordinal))) {
					violations.Add($"{relativePath}:{lineNumber}");
				}
			}
		}

		violations.Should().BeEmpty(
			"every Instant shown in JobTrack.Web must render through InstantDisplay.Format in the viewer's own zone, " +
			"never a raw ToString() of a rate period or cost-trace segment boundary");
	}
}
