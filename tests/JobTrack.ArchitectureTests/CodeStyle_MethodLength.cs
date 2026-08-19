namespace JobTrack.ArchitectureTests;

using Abstractions;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class CodeStyle_MethodLength
{
	[Fact]
	public void Repository_methods_do_not_exceed_the_line_count_guideline()
	{
		var violations = RepositorySourceFiles.CSharpAndRazor()
			.SelectMany(static file => MethodLengthGuard.FindViolations(file, File.ReadAllText(file)))
			.ToArray();

		violations.Should().BeEmpty(
			"a method over {0} executable lines should be decomposed or carry a reviewed " +
			"LongMethodAttribute exception:{1}{2}",
			MethodLengthGuard.MaxLineCount,
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void Method_at_the_ceiling_is_not_a_violation()
	{
		var source = MethodWithExecutableLines(MethodLengthGuard.MaxLineCount);

		MethodLengthGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void Method_over_the_ceiling_is_a_violation()
	{
		var source = MethodWithExecutableLines(MethodLengthGuard.MaxLineCount + 1);

		MethodLengthGuard.FindViolations("Example.cs", source)
			.Should().ContainSingle()
			.Which.Should().Contain("Example.Method")
			.And.Contain($"{MethodLengthGuard.MaxLineCount + 1} executable lines");
	}

	[Fact]
	public void Blank_and_comment_only_lines_do_not_count_as_executable_lines()
	{
		var source = """
			internal sealed class Example
			{
				public void Method()
				{
					// This explanation does not add an executable line.

					_ = 1;
					/* Nor does this one. */
				}
			}
			""";

		MethodLengthGuard.ExecutableLineCount(source).Should().Be(1);
	}

	[Fact]
	public void Method_carrying_LongMethodAttribute_is_excluded_from_the_scan()
	{
		var method = MethodWithExecutableLines(MethodLengthGuard.MaxLineCount + 1);
		var source = method.Replace(
			"public void Method()",
			"[JobTrack.Abstractions.LongMethod(\"Reviewed fixture.\")]\n\tpublic void Method()",
			StringComparison.Ordinal);

		MethodLengthGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void Razor_functions_method_over_the_ceiling_is_a_violation()
	{
		var method = MethodDeclarationWithExecutableLines(MethodLengthGuard.MaxLineCount + 1);
		var source = $"@functions {{{Environment.NewLine}{method}{Environment.NewLine}}}";

		MethodLengthGuard.FindViolations("Example.cshtml", source)
			.Should().ContainSingle()
			.Which.Should().Contain("Method")
			.And.Contain($"{MethodLengthGuard.MaxLineCount + 1} executable lines");
	}

	[Fact]
	public void Razor_generated_rendering_method_is_not_measured()
	{
		var markup = string.Join(Environment.NewLine, Enumerable.Repeat("<p>Authored markup</p>", MethodLengthGuard.MaxLineCount + 1));

		MethodLengthGuard.FindViolations("Example.cshtml", markup).Should().BeEmpty();
	}

	[Fact]
	public void Local_function_is_measured_independently_of_its_containing_method()
	{
		var statements = string.Join(
			Environment.NewLine,
			Enumerable.Range(1, MethodLengthGuard.MaxLineCount + 1).Select(static value => $"\t\t\t_ = {value};"));
		var source = $$"""
			internal sealed class Example
			{
				public void Method()
				{
					_ = 0;
					void Local()
					{
			{{statements}}
					}
				}
			}
			""";

		MethodLengthGuard.FindViolations("Example.cs", source)
			.Should().ContainSingle()
			.Which.Should().Contain("Example.Local");
	}

	[Fact]
	public void LongMethodAttribute_carries_its_reviewed_reason()
	{
		var attribute = new LongMethodAttribute("The linear protocol is easier to audit as one operation.");

		attribute.Reason.Should().Be("The linear protocol is easier to audit as one operation.");
	}

	private static string MethodWithExecutableLines(int lineCount)
	{
		var method = MethodDeclarationWithExecutableLines(lineCount);
		return $$"""
			internal sealed class Example
			{
			{{method}}
			}
			""";
	}

	private static string MethodDeclarationWithExecutableLines(int lineCount)
	{
		var statements = string.Join(Environment.NewLine, Enumerable.Range(1, lineCount).Select(static value => $"\t\t_ = {value};"));
		return $$"""
				public void Method()
				{
			{{statements}}
				}
			""";
	}
}

internal static class MethodLengthGuard
{
	public const int MaxLineCount = 75;

	public static IEnumerable<string> FindViolations(string fileName, string source) =>
		fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
			? FindRazorViolations(fileName, RazorCSharpDocument.Parse(fileName, source))
			: FindCSharpViolations(fileName, source);

	private static IEnumerable<string> FindCSharpViolations(string fileName, string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();
		return root.DescendantNodes()
			.Where(IsExecutableUnit)
			.Where(static declaration => !HasReviewedException(declaration))
			.Select(declaration => (Declaration: declaration, Body: Body(declaration)))
			.Where(static unit => unit.Body is not null)
			.Select(unit => (unit.Declaration, LineCount: ExecutableLineCount(unit.Body!)))
			.Where(static unit => unit.LineCount > MaxLineCount)
			.Select(unit => Describe(fileName, unit.Declaration, unit.LineCount))
			.Order(StringComparer.Ordinal);
	}

	private static IEnumerable<string> FindRazorViolations(string fileName, RazorCSharpDocument document) => document.Root
		.DescendantNodes()
		.Where(IsExecutableUnit)
		.Where(static declaration => !HasReviewedException(declaration))
		.Select(declaration => (
			Declaration: declaration,
			Body: Body(declaration),
			Line: document.OriginalLine(Identifier(declaration))))
		.Where(static unit => unit.Body is not null && unit.Line.HasValue)
		.Select(unit => (
			unit.Declaration,
			Line: unit.Line!.Value,
			LineCount: ExecutableLineCount(unit.Body!, document.OriginalLines)))
		.Where(static unit => unit.LineCount > MaxLineCount)
		.Select(unit => Describe(fileName, unit.Declaration, unit.LineCount, unit.Line))
		.Order(StringComparer.Ordinal);

	public static int ExecutableLineCount(string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();
		var declaration = root.DescendantNodes().First(IsExecutableUnit);
		return ExecutableLineCount(Body(declaration)!);
	}

	private static bool IsExecutableUnit(SyntaxNode node) => node is BaseMethodDeclarationSyntax
		or AccessorDeclarationSyntax
		or LocalFunctionStatementSyntax;

	private static SyntaxNode? Body(SyntaxNode declaration) => declaration switch {
		BaseMethodDeclarationSyntax method => method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression,
		AccessorDeclarationSyntax accessor => accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody?.Expression,
		LocalFunctionStatementSyntax localFunction => localFunction.Body ?? (SyntaxNode?)localFunction.ExpressionBody?.Expression,
		_ => throw new ArgumentOutOfRangeException(nameof(declaration)),
	};

	private static int ExecutableLineCount(SyntaxNode body) => body
		.DescendantTokens(node => node == body || !IsExecutableUnit(node))
		.Where(static token => !token.IsKind(SyntaxKind.OpenBraceToken) && !token.IsKind(SyntaxKind.CloseBraceToken))
		.SelectMany(static token => LinesOccupiedBy(token))
		.Distinct()
		.Count();

	private static int ExecutableLineCount(SyntaxNode body, Func<SyntaxToken, IEnumerable<int>> originalLines) => body
		.DescendantTokens(node => node == body || !IsExecutableUnit(node))
		.Where(static token => !token.IsKind(SyntaxKind.OpenBraceToken) && !token.IsKind(SyntaxKind.CloseBraceToken))
		.SelectMany(originalLines)
		.Distinct()
		.Count();

	private static IEnumerable<int> LinesOccupiedBy(SyntaxToken token)
	{
		var span = token.GetLocation().GetLineSpan();
		var firstLine = span.StartLinePosition.Line;
		var lineCount = span.EndLinePosition.Line - firstLine + 1;
		return Enumerable.Range(firstLine, lineCount);
	}

	private static bool HasReviewedException(SyntaxNode declaration) => AttributeLists(declaration)
		.SelectMany(static list => list.Attributes)
		.Any(static attribute => IsLongMethodAttribute(attribute.Name.ToString()));

	private static SyntaxList<AttributeListSyntax> AttributeLists(SyntaxNode declaration) => declaration switch {
		BaseMethodDeclarationSyntax method => method.AttributeLists,
		AccessorDeclarationSyntax accessor => accessor.AttributeLists,
		LocalFunctionStatementSyntax localFunction => localFunction.AttributeLists,
		_ => throw new ArgumentOutOfRangeException(nameof(declaration)),
	};

	private static bool IsLongMethodAttribute(string name)
	{
		var simpleName = name[(name.LastIndexOfAny(['.', ':']) + 1)..];
		return simpleName is nameof(LongMethodAttribute) or "LongMethod";
	}

	private static string Describe(string fileName, SyntaxNode declaration, int lineCount)
	{
		var line = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
		return Describe(fileName, declaration, lineCount, line);
	}

	private static string Describe(string fileName, SyntaxNode declaration, int lineCount, int line) =>
		$"{fileName}:{line}: {DisplayName(declaration)} has {lineCount} executable lines (> {MaxLineCount})";

	private static SyntaxToken Identifier(SyntaxNode declaration) => declaration switch {
		MethodDeclarationSyntax method => method.Identifier,
		ConstructorDeclarationSyntax constructor => constructor.Identifier,
		DestructorDeclarationSyntax destructor => destructor.Identifier,
		OperatorDeclarationSyntax operation => operation.OperatorToken,
		ConversionOperatorDeclarationSyntax conversion => conversion.ImplicitOrExplicitKeyword,
		AccessorDeclarationSyntax accessor => accessor.Keyword,
		LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
		_ => throw new ArgumentOutOfRangeException(nameof(declaration)),
	};

	private static string DisplayName(SyntaxNode declaration)
	{
		var containingType = declaration.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
		var memberName = declaration switch {
			MethodDeclarationSyntax method => method.Identifier.ValueText,
			ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
			DestructorDeclarationSyntax destructor => $"~{destructor.Identifier.ValueText}",
			OperatorDeclarationSyntax operation => $"operator {operation.OperatorToken.ValueText}",
			ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
			AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
			LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
			_ => throw new ArgumentOutOfRangeException(nameof(declaration)),
		};

		return containingType is not null ? $"{containingType}.{memberName}" : memberName;
	}
}
