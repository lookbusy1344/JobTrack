namespace JobTrack.ArchitectureTests;

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Architecture guard for one predictable answer to "what spaces this block from the next one": the
///     component itself, in <c>site.css</c>, on the <c>--jt-space-*</c> scale. Two mechanisms for one
///     decision is what made the rhythm unguessable — <c>.jt-form-card</c> spaced itself while
///     <c>.jt-card</c> relied on an <c>mb-4</c> at three of its five uses, <c>&lt;dl&gt;</c> declared a
///     margin in CSS *and* an <c>mb-4</c> at both call sites (the utility won, at Bootstrap's off-scale
///     fixed 1.5rem), <c>.jt-toolbar</c> shipped four different answers across sixteen uses, and
///     <c>.jt-notice</c> declared nothing at all, which is how a blocked job's notice came to butt
///     straight against the pill below it.
///     Two halves, one rule: the stylesheet must declare a bottom margin for every block component, and
///     the markup must not restate it. A bottom-margin utility is allowed only as <c>mb-0</c> — a
///     deliberate per-instance cancel, which reads as an exception rather than as a second system.
/// </summary>
public sealed class BlockComponentSpacingArchitectureTests
{
	[Fact]
	public void Every_block_component_declares_its_own_bottom_margin_in_the_stylesheet()
	{
		var stylesheet = File.ReadAllText(Path.Combine(
			RepositoryPaths.SolutionRoot(), "src", "JobTrack.Web", "wwwroot", "css", "site.css"));

		var undeclared = BlockComponentSpacingGuard.BlockComponents
			.Where(component => !BlockComponentSpacingGuard.DeclaresBottomMargin(stylesheet, component))
			.ToArray();

		undeclared.Should().BeEmpty(
			"a block component owns its gap to the next block, so site.css must give each one a "
			+ "margin-bottom on the --jt-space-* scale: {0}",
			string.Join(", ", undeclared));
	}

	[Fact]
	public void No_markup_restates_a_block_components_bottom_margin()
	{
		var violations = RazorViews()
			.SelectMany(static file => BlockComponentSpacingGuard.FindViolations(file, File.ReadAllText(file)))
			.ToArray();

		violations.Should().BeEmpty(
			"a block component's gap is declared once, in site.css:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Theory]
	[InlineData("<div class=\"jt-notice mb-3\"></div>")]
	[InlineData("<div class=\"jt-card mb-4\"></div>")]
	[InlineData("<dl class=\"row g-2 mb-4 jt-record--full\"></dl>")]
	[InlineData("<div class=\"jt-toolbar d-flex gap-2 mb-3\"></div>")]
	[InlineData("<div class=\"jt-table-block my-4\"></div>")]
	public void Restating_a_block_components_margin_in_markup_is_a_violation(string markup) =>
		BlockComponentSpacingGuard.FindViolations("Example.cshtml", markup).Should().NotBeEmpty();

	[Theory]
	[InlineData("<div class=\"jt-notice\"></div>")]
	[InlineData("<div class=\"jt-card\" id=\"status\"></div>")]
	// mb-0 is the sanctioned deviation: an explicit cancel, not a second source of rhythm.
	[InlineData("<ul class=\"jt-list jt-list--wide mb-0\"></ul>")]
	// A *top* margin is a different decision from the component's own trailing gap, and stays a utility.
	[InlineData("<form class=\"jt-form-card mt-3\"></form>")]
	// Utilities on anything that is not a block component are none of this rule's business.
	[InlineData("<dd class=\"w-75 mb-0\"></dd>")]
	[InlineData("<div class=\"d-flex flex-wrap align-items-center gap-2 mb-3\"></div>")]
	public void A_component_left_to_the_stylesheet_or_an_unrelated_utility_is_not_a_violation(string markup) =>
		BlockComponentSpacingGuard.FindViolations("Example.cshtml", markup).Should().BeEmpty();

	private static IEnumerable<string> RazorViews() =>
		Directory.EnumerateFiles(Path.Combine(RepositoryPaths.SolutionRoot(), "src"), "*.cshtml", SearchOption.AllDirectories)
			.Where(static file => {
				var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				return !segments.Contains("bin") && !segments.Contains("obj") && !segments.Contains("lib");
			});
}

internal static partial class BlockComponentSpacingGuard
{
	/// <summary>
	///     The block-level components of the Console design language — every skin whose job includes
	///     standing apart from the block below it. The element-selector members (<c>dl</c>, <c>fieldset</c>)
	///     are spaced by the same rule but carry no class token, so only the class members can be
	///     markup-checked; both halves of the rule still cover them in the stylesheet.
	/// </summary>
	public static readonly FrozenSet<string> BlockComponents = new[] {
		"jt-card", "jt-notice", "jt-toolbar", "jt-form-card", "jt-table-block", "jt-list", "jt-empty", "jt-page-head", "jt-lede", "jt-context", "dl",
		"fieldset",
	}.ToFrozenSet(StringComparer.Ordinal);

	[GeneratedRegex(@"class=""(?<tokens>[^""]*)""", RegexOptions.CultureInvariant)]
	private static partial Regex ClassAttribute();

	// A bottom-margin utility that actually adds space: `mb-1`…`mb-5`, `my-*`, and their breakpoint forms.
	// `mb-0`/`my-0` are excluded deliberately -- an explicit cancel is the sanctioned deviation.
	[GeneratedRegex(
		@"(?<![\w-])m[by]-(?:sm-|md-|lg-|xl-|xxl-)?(?:[1-5]|auto)(?![\w-])",
		RegexOptions.CultureInvariant)]
	private static partial Regex SpacingUtility();

	[GeneratedRegex(@"margin-bottom:\s*var\(--jt-space-[1-5]\)", RegexOptions.CultureInvariant)]
	private static partial Regex BottomMarginDeclaration();

	// Comment-stripped, so a selector named only in prose never counts as a declaration.
	[GeneratedRegex(@"/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
	private static partial Regex Comment();

	public static bool DeclaresBottomMargin(string stylesheet, string component)
	{
		var selector = component is "dl" or "fieldset" ? component : $".{component}";
		return DeclarationBlocks(stylesheet)
			.Any(block => block.Selectors.Contains(selector, StringComparer.Ordinal)
						  && BottomMarginDeclaration().IsMatch(block.Body));
	}

	public static IEnumerable<string> FindViolations(string fileName, string source)
	{
		foreach (Match attribute in ClassAttribute().Matches(source)) {
			var tokens = attribute.Groups["tokens"].Value;
			// A class token names the component in most cases; `dl`/`fieldset` are spaced as elements, so
			// their utilities hide in the class attribute of a tag whose class list never mentions them --
			// which is exactly where both <dl>s were restating `mb-4`. Read the enclosing tag name too.
			var component = tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries)
								.FirstOrDefault(BlockComponents.Contains)
							?? EnclosingElement(source, attribute.Index);
			if (component is null || !SpacingUtility().IsMatch(tokens)) {
				continue;
			}

			var line = source.Take(attribute.Index).Count(static c => c == '\n') + 1;
			yield return $"{Path.GetFileName(fileName)}:{line}: '{component}' restates its own margin "
						 + $"in the markup ('{tokens}') — a block component's gap is declared once, in site.css";
		}
	}

	/// <summary>
	///     The tag name owning the attribute at <paramref name="attributeIndex" />, when that element is
	///     itself a block component; <see langword="null" /> for every other tag.
	/// </summary>
	private static string? EnclosingElement(string source, int attributeIndex)
	{
		var tagStart = source.LastIndexOf('<', attributeIndex);
		if (tagStart < 0) {
			return null;
		}

		var name = source[(tagStart + 1)..attributeIndex].TrimStart();
		var end = name.IndexOfAny([' ', '\t', '\r', '\n', '>', '/']);
		name = end < 0 ? name : name[..end];
		return BlockComponents.Contains(name) ? name : null;
	}

	private static IEnumerable<(IReadOnlyList<string> Selectors, string Body)> DeclarationBlocks(string stylesheet)
	{
		var source = Comment().Replace(stylesheet, string.Empty);
		foreach (var block in source.Split('}')) {
			var brace = block.IndexOf('{', StringComparison.Ordinal);
			if (brace < 0) {
				continue;
			}

			var selectors = block[..brace]
				.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
				// A nested at-rule (`@media …{`) leaves its prelude on the front of the first selector.
				.Select(static selector => selector[(selector.LastIndexOf('{') + 1)..].Trim())
				.ToArray();
			yield return (selectors, block[(brace + 1)..]);
		}
	}
}
