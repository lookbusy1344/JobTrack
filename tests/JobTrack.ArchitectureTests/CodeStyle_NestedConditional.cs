namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Architecture guard for CLAUDE.md's ban on nested <c>?:</c> — a ternary inside either branch of another
///     ternary, in any position. The chained <c>a ? x : b ? y : z</c> form is the common offender; it hides a
///     multi-way decision inside an expression that reads as two-way. Rewrite as a <c>switch</c> expression, or
///     extract the inner conditional into a named local.
/// </summary>
/// <remarks>
///     Roslyn cannot parse Razor, so <c>.cshtml</c> files are covered by lifting out their explicit C# regions —
///     <c>@(…)</c> expressions and <c>@{…}</c> blocks — and parsing each one on its own. A ternary cannot appear
///     in an implicit expression (<c>@foo.Bar</c>) without parentheses, so those two forms are the whole surface.
/// </remarks>
public sealed class CodeStyle_NestedConditional
{
	[Fact]
	public void Repository_sources_do_not_nest_conditional_expressions()
	{
		var violations = SourceFiles()
						 .SelectMany(static file => NestedConditionalGuard.FindViolations(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty("nested conditionals found:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, violations));
	}

	[Theory]
	[InlineData("var y = a ? 1 : b ? 2 : 3;")] // chained — nested in the false branch
	[InlineData("var y = a ? (b ? 1 : 2) : 3;")] // nested in the true branch
	[InlineData("var y = a ? 1 : (b ? 2 : 3);")] // parenthesised chain
	[InlineData("var y = (a ? b : c) ? 1 : 2;")] // nested in the condition
	public void Nested_conditionals_are_violations(string statement)
	{
		var source = $"class Example {{ void M(bool a, bool b, bool c) {{ {statement} }} }}";

		NestedConditionalGuard.FindViolations("Example.cs", source).Should().NotBeEmpty();
	}

	[Theory]
	[InlineData("var y = a ? 1 : 2;")] // single conditional
	[InlineData("var y = (a ? 1 : 2) + (b ? 3 : 4);")] // siblings, not nested
	[InlineData("M(a ? 1 : 2, b ? 3 : 4);")] // siblings as separate arguments
	[InlineData("var y = a ? 1 : 2; var z = b ? 3 : 4;")] // separate statements
	public void Unnested_conditionals_are_allowed(string statement)
	{
		var source = $"class Example {{ void M(bool a, bool b) {{ {statement} }} static void M(int p, int q) {{ }} }}";

		NestedConditionalGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Theory]
	[InlineData("<p>@(a ? \"x\" : b ? \"y\" : \"z\")</p>")]
	[InlineData("<p class=\"@(a ? (b ? \"x\" : \"y\") : null)\">text</p>")]
	[InlineData("@{ var label = a ? \"x\" : b ? \"y\" : \"z\"; }")]
	public void Razor_nested_conditionals_are_violations(string markup) => NestedConditionalGuard.FindViolations("Example.cshtml", markup).Should().NotBeEmpty();

	[Theory]
	[InlineData("<p>@(a ? \"x\" : \"y\")</p>")]
	[InlineData("<p>@(a ? \"x\" : \"y\")@(b ? \"z\" : \"w\")</p>")] // siblings, not nested
	[InlineData("@{ var label = a ? \"x\" : \"y\"; var other = b ? \"z\" : \"w\"; }")]
	[InlineData("<a href=\"https://example.test/a?b=1\">link</a>")] // a query string is not a conditional
	public void Razor_unnested_conditionals_are_allowed(string markup) => NestedConditionalGuard.FindViolations("Example.cshtml", markup).Should().BeEmpty();

	private static IEnumerable<string> SourceFiles()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		foreach (var top in (string[])["src", "tests", "samples"]) {
			var directory = Path.Combine(solutionRoot, top);
			foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
										  .Concat(Directory.EnumerateFiles(directory, "*.cshtml", SearchOption.AllDirectories))
										  .Where(static file => !IsGeneratedOutput(file))) {
				yield return file;
			}
		}
	}

	private static bool IsGeneratedOutput(string file)
	{
		var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return segments.Contains("bin") || segments.Contains("obj");
	}
}

internal static class NestedConditionalGuard
{
	public static IEnumerable<string> FindViolations(string fileName, string source) =>
		fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
			? RazorRegions(source).SelectMany(region => NestedIn(fileName, region.Code, region.Line))
			: NestedIn(fileName, source, 0);

	private static IEnumerable<string> NestedIn(string fileName, string code, int lineOffset)
	{
		var root = CSharpSyntaxTree.ParseText(code).GetRoot();
		return root.DescendantNodes()
				   .OfType<ConditionalExpressionSyntax>()
				   .Where(static conditional => conditional.Ancestors().OfType<ConditionalExpressionSyntax>().Any())
				   .Select(conditional => Describe(fileName, conditional, lineOffset));
	}

	private static string Describe(string fileName, ConditionalExpressionSyntax conditional, int lineOffset)
	{
		var line = conditional.GetLocation().GetLineSpan().StartLinePosition.Line + 1 + lineOffset;
		return $"{Path.GetFileName(fileName)}:{line}: forbidden nested conditional expression";
	}

	// The explicit C# regions of a Razor file: `@(expression)` and `@{ statements }`. Each is returned as
	// standalone C# (an expression body, or the block's contents) along with its zero-based starting line.
	private static IEnumerable<(string Code, int Line)> RazorRegions(string source)
	{
		for (var index = 0; index < source.Length - 1; ++index) {
			if (source[index] != '@') {
				continue;
			}

			var opener = source[index + 1];
			if (opener is not ('(' or '{')) {
				continue;
			}

			var end = MatchingBrace(source, index + 1);
			if (end < 0) {
				continue;
			}

			var inner = source[(index + 2)..end];
			var line = source.Take(index).Count(static c => c == '\n');
			yield return (opener == '(' ? $"_ = {inner};" : inner, line);
			index = end;
		}
	}

	// Index of the delimiter closing the one at <paramref name="start" />, or -1 when unbalanced. String and
	// character literals are skipped so that a bracket inside them cannot unbalance the scan.
	private static int MatchingBrace(string source, int start)
	{
		var open = source[start];
		var close = open == '(' ? ')' : '}';
		var depth = 0;
		for (var index = start; index < source.Length; ++index) {
			var current = source[index];
			switch (current) {
				case '"' or '\'':
					index = EndOfLiteral(source, index);
					break;
				case var _ when current == open:
					++depth;
					break;
				case var _ when current == close && --depth == 0:
					return index;
			}
		}

		return -1;
	}

	private static int EndOfLiteral(string source, int start)
	{
		var quote = source[start];
		for (var index = start + 1; index < source.Length; ++index) {
			switch (source[index]) {
				case '\\':
					++index;
					break;
				case var current when current == quote:
					return index;
			}
		}

		return source.Length - 1;
	}
}
