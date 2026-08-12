namespace JobTrack.ArchitectureTests;

using System.Text.RegularExpressions;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Architecture guard for the house rule that a page scrolls as one unit: nothing inside it may be
///     its own scrolling region. A nested scroller hides content behind a scrollbar the reader never
///     sees on a touch device, fights the page's own scroll gesture, clips any popover opened inside it,
///     and pins <c>position: sticky</c> to the inner box instead of the viewport. Scans the hand-written
///     stylesheets and the Razor markup under <c>src</c> for both spellings of the mistake: an
///     <c>overflow</c> declaration whose value scrolls, and Bootstrap's own scrolling utilities.
///     <c>overflow: hidden</c> and <c>overflow: clip</c> are unaffected — they clip without scrolling.
/// </summary>
public sealed class NestedScrollingRegionArchitectureTests
{
	[Fact]
	public void Hand_written_stylesheets_declare_no_scrolling_overflow()
	{
		var violations = StyleSheets()
						 .SelectMany(static file => NestedScrollingRegionGuard.FindStyleViolations(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty(
			"a page must scroll as one unit — no element inside it may scroll on its own:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void Razor_markup_uses_no_scrolling_layout_utility()
	{
		var violations = RazorViews()
						 .SelectMany(static file => NestedScrollingRegionGuard.FindMarkupViolations(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty(
			"a page must scroll as one unit — no element inside it may scroll on its own:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Theory]
	[InlineData("overflow: auto;")]
	[InlineData("overflow:scroll;")]
	[InlineData("overflow-x: auto;")]
	[InlineData("overflow-y: scroll;")]
	[InlineData("-webkit-overflow-scrolling: touch;")]
	public void Scrolling_overflow_declarations_are_violations(string declaration) =>
		NestedScrollingRegionGuard.FindStyleViolations("site.css", $".example {{ {declaration} }}").Should().NotBeEmpty();

	[Theory]
	[InlineData("overflow: hidden;")]
	[InlineData("overflow: clip;")]
	[InlineData("overflow: visible;")]
	[InlineData("overflow-wrap: break-word;")]
	[InlineData("text-overflow: ellipsis;")]
	public void Clipping_and_wrapping_declarations_are_not_violations(string declaration) =>
		NestedScrollingRegionGuard.FindStyleViolations("site.css", $".example {{ {declaration} }}").Should().BeEmpty();

	[Theory]
	[InlineData("<div class=\"table-responsive\"></div>")]
	[InlineData("<div class=\"card overflow-auto\"></div>")]
	[InlineData("<div class=\"overflow-y-scroll\"></div>")]
	[InlineData("<div style=\"overflow-x: auto\"></div>")]
	public void Scrolling_layout_utilities_are_violations(string markup) =>
		NestedScrollingRegionGuard.FindMarkupViolations("Example.cshtml", markup).Should().NotBeEmpty();

	[Theory]
	[InlineData("<div class=\"overflow-hidden\"></div>")]
	[InlineData("<div class=\"jt-table-block\"></div>")]
	[InlineData("<p>The table is responsive.</p>")]
	public void Non_scrolling_markup_is_not_a_violation(string markup) =>
		NestedScrollingRegionGuard.FindMarkupViolations("Example.cshtml", markup).Should().BeEmpty();

	// The vendored Bootstrap build under wwwroot/lib is pinned by libman and never hand-edited, so it is
	// not this rule's business; the rule binds the stylesheets this repository actually writes.
	private static IEnumerable<string> StyleSheets() =>
		Directory.EnumerateFiles(Path.Combine(RepositoryPaths.SolutionRoot(), "src"), "*.css", SearchOption.AllDirectories)
				 .Where(static file => !IsExcluded(file));

	private static IEnumerable<string> RazorViews() =>
		Directory.EnumerateFiles(Path.Combine(RepositoryPaths.SolutionRoot(), "src"), "*.cshtml", SearchOption.AllDirectories)
				 .Where(static file => !IsExcluded(file));

	private static bool IsExcluded(string file)
	{
		var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return segments.Contains("bin") || segments.Contains("obj") || segments.Contains("lib");
	}
}

internal static partial class NestedScrollingRegionGuard
{
	// `overflow`, `overflow-x`, `overflow-y` set to a scrolling value, plus the iOS momentum-scroll
	// property that only makes sense on one. Anchored on the property name so `overflow-wrap` and
	// `text-overflow` (neither of which scrolls anything) stay out of it.
	[GeneratedRegex(
		@"(?<![\w-])(?:overflow(?:-[xy])?\s*:\s*(?:auto|scroll)|-webkit-overflow-scrolling\s*:)",
		RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex ScrollingDeclaration();

	// Bootstrap's scrolling utilities and its horizontally scrolling table wrapper, as class tokens.
	[GeneratedRegex(
		@"(?<![\w-])(?:table-responsive(?:-(?:sm|md|lg|xl|xxl))?|overflow-(?:auto|scroll)|overflow-[xy]-(?:auto|scroll))(?![\w-])",
		RegexOptions.CultureInvariant)]
	private static partial Regex ScrollingUtility();

	public static IEnumerable<string> FindStyleViolations(string fileName, string source) =>
		Describe(fileName, source, ScrollingDeclaration(), "element declares its own scrolling region");

	public static IEnumerable<string> FindMarkupViolations(string fileName, string source) =>
		Describe(fileName, source, ScrollingUtility(), "scrolling layout utility")
			.Concat(Describe(fileName, source, ScrollingDeclaration(), "element declares its own scrolling region"));

	private static IEnumerable<string> Describe(string fileName, string source, Regex pattern, string reason)
	{
		foreach (Match match in pattern.Matches(source)) {
			var line = source.Take(match.Index).Count(static c => c == '\n') + 1;
			yield return $"{Path.GetFileName(fileName)}:{line}: {reason} ('{match.Value}') — a page must scroll as one unit";
		}
	}
}
