namespace JobTrack.ArchitectureTests;

using System.Text.RegularExpressions;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Keeps responsive table allocation in Bootstrap's twelve-column model. A table without a
///     <c>col-*</c> allocation falls back to content-driven auto layout, which can compress the
///     primary column while leaving most of a compact action column visually empty. Hand-written
///     percentage widths in <c>site.css</c> are not a second responsive layout system.
/// </summary>
public sealed class ResponsiveTableColumnArchitectureTests
{
	[Fact]
	public void Every_application_table_uses_a_bootstrap_column_allocation()
	{
		var violations = RazorViews()
						 .SelectMany(static file => ResponsiveTableColumnGuard.FindTablesWithoutBootstrapColumns(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty(
			"responsive table widths belong in Bootstrap column classes:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void Hand_written_css_does_not_allocate_table_columns_with_percentages()
	{
		var stylesheet = File.ReadAllText(Path.Combine(
			RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "wwwroot", "css", "site.css"));

		ResponsiveTableColumnGuard.FindPercentageColumnWidths("site.css", stylesheet).Should().BeEmpty(
			"responsive table widths belong in Bootstrap column classes, not percentage declarations in site.css");
	}

	[Theory]
	[InlineData("<table class=\"table\"><th class=\"col-10 col-xl-4\">Name</th></table>", false)]
	[InlineData("<table class=\"table\"><th>Name</th></table>", true)]
	public void A_table_without_a_bootstrap_column_is_a_violation(string markup, bool expectedViolation) =>
		ResponsiveTableColumnGuard.FindTablesWithoutBootstrapColumns("Example.cshtml", markup).Any().Should().Be(expectedViolation);

	[Theory]
	[InlineData(".jt-tree-cell { width: 34%; }")]
	[InlineData(".table td.jt-name { min-width: 75%; }")]
	public void Percentage_table_column_width_is_a_violation(string css) =>
		ResponsiveTableColumnGuard.FindPercentageColumnWidths("site.css", css).Should().NotBeEmpty();

	[Theory]
	[InlineData(".jt-tree-icon { width: 1rem; }")]
	[InlineData(".container { max-width: 70rem; }")]
	public void Non_table_visual_width_is_not_a_violation(string css) =>
		ResponsiveTableColumnGuard.FindPercentageColumnWidths("site.css", css).Should().BeEmpty();

	private static IEnumerable<string> RazorViews() =>
		Directory.EnumerateFiles(Path.Combine(RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "Pages"), "*.cshtml",
					 SearchOption.AllDirectories)
				 .Where(static file => {
					 var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					 return !segments.Contains("bin") && !segments.Contains("obj") && !segments.Contains("lib");
				 });
}

internal static partial class ResponsiveTableColumnGuard
{
	[GeneratedRegex(@"<table\b(?<attributes>[^>]*)>(?<body>.*?)</table>",
		RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
	private static partial Regex Table();

	[GeneratedRegex(@"(?<![\w-])col-(?:(?:sm|md|lg|xl|xxl)-)?(?:auto|[1-9]|1[0-2])(?![\w-])", RegexOptions.CultureInvariant)]
	private static partial Regex BootstrapColumn();

	[GeneratedRegex(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
	private static partial Regex DeclarationBlock();

	[GeneratedRegex(@"(?:min-|max-)?width\s*:\s*[^;}]*%", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex PercentageWidth();

	[GeneratedRegex(@"(?:^|[\s,>+~])(?:th|td)(?=$|[\s.#:\[,>+~])|\.jt-col-|\.jt-tree-cell", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex TableColumnSelector();

	public static IEnumerable<string> FindTablesWithoutBootstrapColumns(string fileName, string source)
	{
		foreach (Match table in Table().Matches(source)) {
			if (BootstrapColumn().IsMatch(table.Value)) {
				continue;
			}

			var line = source.Take(table.Index).Count(static character => character == '\n') + 1;
			yield return $"{Path.GetFileName(fileName)}:{line}: table has no Bootstrap col-* allocation";
		}
	}

	public static IEnumerable<string> FindPercentageColumnWidths(string fileName, string source)
	{
		foreach (Match block in DeclarationBlock().Matches(source)) {
			var selector = block.Groups["selector"].Value;
			var commentEnd = selector.LastIndexOf("*/", StringComparison.Ordinal);
			if (commentEnd >= 0) {
				selector = selector[(commentEnd + 2)..];
			}

			var isTableColumn = TableColumnSelector().IsMatch(selector);
			if (!isTableColumn || !PercentageWidth().IsMatch(block.Groups["body"].Value)) {
				continue;
			}

			var line = source.Take(block.Index).Count(static character => character == '\n') + 1;
			yield return $"{Path.GetFileName(fileName)}:{line}: percentage table-column width ('{block.Groups["body"].Value.Trim()}')";
		}
	}
}
