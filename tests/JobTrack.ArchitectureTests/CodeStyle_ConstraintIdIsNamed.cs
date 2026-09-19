namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Architecture guard for §2.8 of the 2026-09-18 fresh-eyes remediation: every
///     <c>InvariantViolationException</c> constraint id is a named <c>ConstraintIds</c> member, not a
///     string literal typed independently at each throw site -- a throw site and the catch site that
///     switches on the same id (<c>WorkSessionFailureDisplay</c>, the web pages' <c>ConstraintId ==</c>
///     checks) previously carried two independently typed literals, where a typo in either one compiles
///     silently. <see cref="JobTrack.Abstractions.ConstraintIds" /> itself is exempt, since its own
///     members are where the literal values live.
/// </summary>
public sealed class CodeStyle_ConstraintIdIsNamed
{
	private const string ConstraintIdsFile = "ConstraintIds.cs";

	[Fact]
	public void Every_InvariantViolationException_constraint_id_argument_is_a_named_member()
	{
		var violations = SourceFiles()
						 .SelectMany(static file => ConstraintIdLiteralGuard.FindViolations(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty(
			"the first constructor argument to InvariantViolationException must be a ConstraintIds member, not a string literal:{0}{1}",
			Environment.NewLine, string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void A_string_literal_constraint_id_is_a_violation()
	{
		const string source = """
							  class Example
							  {
							  	void M() => throw new JobTrack.Abstractions.InvariantViolationException("some-id", "message");
							  }
							  """;

		ConstraintIdLiteralGuard.FindViolations("Example.cs", source).Should().NotBeEmpty();
	}

	[Fact]
	public void A_string_literal_constraint_id_with_an_interpolated_message_is_a_violation()
	{
		const string source = """
							  class Example
							  {
							  	void M(int id) => throw new JobTrack.Abstractions.InvariantViolationException("some-id", $"Node {id} is invalid.");
							  }
							  """;

		ConstraintIdLiteralGuard.FindViolations("Example.cs", source).Should().NotBeEmpty();
	}

	[Fact]
	public void A_ConstraintIds_member_reference_is_allowed()
	{
		const string source = """
							  class Example
							  {
							  	void M() => throw new JobTrack.Abstractions.InvariantViolationException(
							  		JobTrack.Abstractions.ConstraintIds.SomeId, "message");
							  }
							  """;

		ConstraintIdLiteralGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void A_message_only_constructor_call_is_allowed()
	{
		const string source = """
							  class Example
							  {
							  	void M() => throw new JobTrack.Abstractions.InvariantViolationException("just a message");
							  }
							  """;

		ConstraintIdLiteralGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void A_message_and_inner_exception_constructor_call_is_allowed()
	{
		const string source = """
							  class Example
							  {
							  	void M(System.Exception ex) => throw new JobTrack.Abstractions.InvariantViolationException("just a message", ex);
							  }
							  """;

		ConstraintIdLiteralGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	private static string[] SourceFiles()
	{
		var srcRoot = Path.Combine(RepositoryPaths.SolutionRoot(), "src");
		var matches = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
							   .Where(static file =>
								   file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is var segments
								   && !segments.Contains("bin") && !segments.Contains("obj")
								   && Path.GetFileName(file) != ConstraintIdsFile)
							   .ToArray();
		matches.Should().NotBeEmpty("the guard must find source files to be meaningful");

		return matches;
	}
}

internal static class ConstraintIdLiteralGuard
{
	public static IEnumerable<string> FindViolations(string fileName, string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();
		foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(IsInvariantViolationException)) {
			var arguments = creation.ArgumentList?.Arguments ?? [];
			// The one-argument constructor is always message-only (no ConstraintId). A 2-argument call
			// is ambiguous by arity alone between (message, innerException) and (constraintId,
			// message): disambiguated here by the second argument's shape -- the (message,
			// innerException) overload's second argument is always an exception expression, never a
			// string, so a 2-argument call whose second argument is itself string-shaped (a literal or
			// an interpolated string) is the (constraintId, message) overload. A 3+-argument call is
			// always constraintId-carrying.
			var isConstraintIdCarrying = arguments.Count >= 3
										 || arguments.Count == 2 && arguments[1].Expression is LiteralExpressionSyntax {
											 Token.RawKind: (int)SyntaxKind.StringLiteralToken,
										 } or InterpolatedStringExpressionSyntax;
			if (!isConstraintIdCarrying) {
				continue;
			}

			if (arguments[0].Expression is LiteralExpressionSyntax { Token.RawKind: (int)SyntaxKind.StringLiteralToken }) {
				var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
				yield return $"{Path.GetFileName(fileName)}:{line}: InvariantViolationException constraint id must be a ConstraintIds member";
			}
		}
	}

	private static bool IsInvariantViolationException(ObjectCreationExpressionSyntax creation) =>
		creation.Type is IdentifierNameSyntax { Identifier.ValueText: "InvariantViolationException" }
			or QualifiedNameSyntax { Right.Identifier.ValueText: "InvariantViolationException" };
}
