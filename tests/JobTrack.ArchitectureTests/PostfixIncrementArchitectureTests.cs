namespace JobTrack.ArchitectureTests;

using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Architecture guard for the house preference for prefix increment/decrement wherever the operator's
///     result is discarded: a bare <c>count++;</c> statement and a <c>for</c> loop's <c>i++</c> incrementor
///     both mean exactly what <c>++count</c> / <c>++i</c> mean, so the prefix form is used consistently and
///     the postfix form is reserved for the cases that genuinely read the pre-increment value
///     (<c>var b = a++;</c>, <c>buffer[i++] = x;</c>, <c>return count--;</c>), where it stays untouched.
///     Scans the tracked <c>.cs</c> and <c>.cshtml</c> sources under <c>src</c>, <c>tests</c>, and
///     <c>samples</c>.
/// </summary>
public sealed class PostfixIncrementArchitectureTests
{
	[Fact]
	public void Repository_sources_do_not_discard_a_postfix_increment_result()
	{
		var violations = SourceFiles()
						 .SelectMany(static file => PostfixIncrementGuard.FindViolations(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty(
			"the prefix form should be used wherever the result is discarded:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Theory]
	[InlineData("count++;")]
	[InlineData("count--;")]
	[InlineData("this.count++;")]
	[InlineData("values[0]++;")]
	[InlineData("for (var i = 0; i < 10; i++) { }")]
	[InlineData("for (var i = 10; i > 0; i--) { }")]
	[InlineData("for (var i = 0, j = 0; i < 10; i++, j++) { }")]
	[InlineData("while (count < 10) { count++; }")]
	[InlineData("while (count < 10) count++;")] // no braces — still an expression statement
	[InlineData("do { count++; } while (count < 10);")]
	public void Discarded_postfix_result_is_a_violation(string statement)
	{
		var source = $"class Example {{ int count; void M(int[] values) {{ {statement} }} }}";

		PostfixIncrementGuard.FindViolations("Example.cs", source).Should().NotBeEmpty();
	}

	[Theory]
	[InlineData("++count;")] // already the prefix form
	[InlineData("for (var i = 0; i < 10; ++i) { }")] // prefix incrementor
	[InlineData("var b = count++;")] // result used — the pre-increment value is the point
	[InlineData("values[count++] = 1;")] // result used as an index
	[InlineData("M2(count--);")] // result used as an argument
	[InlineData("if (count++ > 0) { }")] // result used as a condition
	[InlineData("while (count++ < 10) { }")] // result used as a loop condition
	[InlineData("do { } while (count-- > 0);")] // result used as a loop condition
	public void Used_or_prefix_forms_are_not_violations(string statement)
	{
		var source = $"class Example {{ int count; void M(int[] values) {{ {statement} }} static void M2(int v) {{ }} }}";

		PostfixIncrementGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Theory]
	[InlineData("@for (var i = 0; i < Model.Rows.Count; i++) { <p>@i</p> }")]
	[InlineData("@{ counter++; }")]
	[InlineData("@while (counter < 10) { counter++; <p>@counter</p> }")]
	[InlineData("@do { counter++; } while (counter < 10)")]
	public void Razor_discarded_postfix_result_is_a_violation(string source) =>
		PostfixIncrementGuard.FindViolations("Example.cshtml", source).Should().NotBeEmpty();

	[Theory]
	[InlineData("<div class=\"jt-card jt-card--narrow\"></div>")]
	[InlineData("<option value=\"\">-- none --</option>")]
	[InlineData("@* a comment -- with dashes *@")]
	[InlineData("@for (var i = 0; i < Model.Rows.Count; ++i) { <p>@i</p> }")]
	[InlineData("@{ var id = Next(counter++); }")] // result used
	[InlineData("@while (counter++ < 10) { <p>@counter</p> }")] // result used as a loop condition
	[InlineData("<div style=\"color: var(--bs-body-color)\"></div>")]
	public void Razor_markup_and_prefix_forms_are_not_violations(string source) =>
		PostfixIncrementGuard.FindViolations("Example.cshtml", source).Should().BeEmpty();

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

internal static partial class PostfixIncrementGuard
{
	// The .cshtml files embed C# but Roslyn cannot parse Razor, so scan their raw text for the two discarded
	// shapes: the `x++;` statement, and the incrementor closing a `for` header. Anchoring each alternative
	// that tightly keeps both markup (`jt-card--narrow`, `-- none --`, CSS `--bs-*` custom properties) and
	// genuine value-using postfixes (`new(_nextId++)`) out of it.
	[GeneratedRegex(
		@"(?:[;{}\s][A-Za-z_][\w.]*(?:\+\+|--)\s*;)|(?:\bfor\s*\([^)]*;[^)]*;\s*[A-Za-z_][\w.]*(?:\+\+|--)\s*\))",
		RegexOptions.CultureInvariant)]
	private static partial Regex RazorDiscardedPostfix();

	public static IEnumerable<string> FindViolations(string fileName, string source) =>
		fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
			? RazorViolations(fileName, source)
			: SyntaxViolations(fileName, source);

	private static IEnumerable<string> RazorViolations(string fileName, string source)
	{
		foreach (Match match in RazorDiscardedPostfix().Matches(source)) {
			var line = source.Take(match.Index).Count(static c => c == '\n') + 1;
			yield return Describe(fileName, line);
		}
	}

	private static IEnumerable<string> SyntaxViolations(string fileName, string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();
		return root.DescendantNodes()
				   .OfType<PostfixUnaryExpressionSyntax>()
				   .Where(IsIncrementOrDecrement)
				   .Where(IsResultDiscarded)
				   .Select(expression => Describe(fileName, expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
	}

	private static bool IsIncrementOrDecrement(PostfixUnaryExpressionSyntax expression) =>
		expression.IsKind(SyntaxKind.PostIncrementExpression) || expression.IsKind(SyntaxKind.PostDecrementExpression);

	// Two shapes throw the operator's value away, so `a++` and `++a` are interchangeable: a bare expression
	// statement, and a `for` header's incrementor list. Anywhere else the pre-increment value may be read, and
	// syntax alone cannot prove otherwise — leave those alone.
	private static bool IsResultDiscarded(PostfixUnaryExpressionSyntax expression) =>
		expression.Parent switch {
			ExpressionStatementSyntax => true,
			ForStatementSyntax parent => parent.Incrementors.Contains(expression),
			_ => false,
		};

	private static string Describe(string fileName, int line) =>
		$"{Path.GetFileName(fileName)}:{line}: discarded postfix increment/decrement — use the prefix form";
}
