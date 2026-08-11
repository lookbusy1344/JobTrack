namespace JobTrack.ArchitectureTests;

using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Architecture guard for CLAUDE.md's ban on the empty property pattern with binding
///     (<c>x is { } y</c> / <c>x is not { } y</c>). It hides a null test and a rebind behind empty braces;
///     a named type pattern (<c>x is SomeType y</c>) is real pattern matching and stays fine. This test scans
///     the tracked <c>.cs</c> and <c>.cshtml</c> sources under <c>src</c>, <c>tests</c>, and <c>samples</c> so
///     the pattern cannot creep back in.
/// </summary>
public sealed class EmptyBracesPatternArchitectureTests
{
	[Fact]
	public void Repository_sources_do_not_use_the_empty_braces_property_pattern()
	{
		var violations = SourceFiles()
			.SelectMany(static file => EmptyBracesPatternGuard.FindViolations(file, File.ReadAllText(file)))
			.ToArray();

		violations.Should().BeEmpty();
	}

	[Theory]
	[InlineData("if (x is { } y) { }")]
	[InlineData("if (x is not { } y) { }")]
	[InlineData("var z = x is { } y ? y : null;")]
	[InlineData("_ = value is not {  } bound;")]
	public void Empty_braces_binding_is_a_violation(string statement)
	{
		var source = $"class Example {{ void M(object? x, object? value) {{ {statement} }} }}";

		EmptyBracesPatternGuard.FindViolations("Example.cs", source).Should().NotBeEmpty();
	}

	[Theory]
	[InlineData("if (x is string y) { }")] // named type pattern — allowed
	[InlineData("if (x is not string y) { }")] // negated named type pattern — allowed
	[InlineData("if (x is not null) { }")] // plain null guard — no binding
	[InlineData("if (x is { Length: 0 } y) { }")] // non-empty property pattern — real matching
	[InlineData("if (x is (var a, var b)) { _ = a; _ = b; }")] // positional pattern — allowed
	public void Allowed_patterns_are_not_violations(string statement)
	{
		var source = $"class Example {{ void M(object? x, (int, int) t) {{ {statement} }} }}";

		EmptyBracesPatternGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void Razor_empty_braces_binding_is_a_violation()
	{
		const string source = "@if (Model.Results is not { } results) { <p>@results</p> }";

		EmptyBracesPatternGuard.FindViolations("Example.cshtml", source).Should().NotBeEmpty();
	}

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

internal static partial class EmptyBracesPatternGuard
{
	// The .cshtml files embed C# but Roslyn cannot parse Razor, so scan their raw text: `is [not] { } binder`.
	[GeneratedRegex(@"\bis\s+(?:not\s+)?\{\s*\}\s+[A-Za-z_]\w*", RegexOptions.CultureInvariant)]
	private static partial Regex RazorEmptyBraces();

	public static IEnumerable<string> FindViolations(string fileName, string source) =>
		fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
			? RazorViolations(fileName, source)
			: SyntaxViolations(fileName, source);

	private static IEnumerable<string> RazorViolations(string fileName, string source)
	{
		foreach (Match match in RazorEmptyBraces().Matches(source)) {
			var line = source.Take(match.Index).Count(static c => c == '\n') + 1;
			yield return $"{Path.GetFileName(fileName)}:{line}: forbidden empty-braces property pattern";
		}
	}

	private static IEnumerable<string> SyntaxViolations(string fileName, string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();
		return root.DescendantNodes()
			.OfType<RecursivePatternSyntax>()
			.Where(IsEmptyBracesBinding)
			.Select(pattern => Describe(fileName, pattern));
	}

	// `{ } y` and — under a `not` — `not { } y`: an empty property pattern that both binds a designation and
	// carries no type or positional clause. A named type pattern (`SomeType y`) is a DeclarationPattern, not a
	// RecursivePattern; a positional pattern populates PositionalPatternClause; a non-empty `{ Prop: ... }`
	// populates Subpatterns — none of those match here.
	private static bool IsEmptyBracesBinding(RecursivePatternSyntax pattern) =>
		pattern is { Type: null, PositionalPatternClause: null, Designation: not null, PropertyPatternClause.Subpatterns.Count: 0 };

	private static string Describe(string fileName, RecursivePatternSyntax pattern)
	{
		var line = pattern.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
		return $"{Path.GetFileName(fileName)}:{line}: forbidden empty-braces property pattern";
	}
}
