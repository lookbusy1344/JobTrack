namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Architecture guard for §2.1 of the 2026-09-18 fresh-eyes remediation: in the work-session
///     command port, a transaction <c>CommitAsync</c> wrapped in a <c>try</c> to translate write
///     conflicts must also translate the optimistic-concurrency failure -- its <c>try</c>'s catches
///     must include <c>DbUpdateConcurrencyException</c>. The original <c>StartWorkAsync</c> caught only
///     <c>ClassifyWriteConflict</c> outcomes, so a concurrent first-start's <c>leaf_work</c> row-version
///     conflict leaked the raw EF exception out of the library. A bare commit (outside any <c>try</c>)
///     is untouched -- it carries no partial-translation intent.
///     <para>
///         Scope is the work-session port, the file where every start/finish/pause/complete path applies
///         EF-side optimistic-concurrency updates that can raise <c>DbUpdateConcurrencyException</c>. The
///         structural provider ports (Move via <c>move_job_node</c>, schedule/rate inserts) also wrap a
///         commit to translate write conflicts, but their concurrency check is server-side SQL or absent
///         (pure inserts), so they cannot raise the EF exception and a broad rule would only mis-fire on
///         them. Their commit auditing belongs with the Stage 2 failure classifier and the Stage 5
///         ADR 0064 port sharing, which examine each port's concurrency behaviour directly.
///     </para>
/// </summary>
public sealed class CodeStyle_CommitInsideTranslation
{
	private const string WorkSessionCommandPortFile = "WorkSessionCommandPort.cs";

	[Fact]
	public void Work_session_commits_wrapped_for_translation_also_catch_concurrency_conflicts()
	{
		var violations = WorkSessionCommandPortFiles()
						 .SelectMany(static file => CommitTranslationGuard.FindViolations(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty(
			"a CommitAsync wrapped in a translating try must also catch DbUpdateConcurrencyException:{0}{1}",
			Environment.NewLine, string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void A_try_wrapped_commit_without_a_concurrency_catch_is_a_violation()
	{
		const string source = """
							  class Example
							  {
							  	async System.Threading.Tasks.Task M(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
							  	{
							  		try {
							  			await transaction.CommitAsync();
							  		}
							  		catch (System.Exception) { throw; }
							  	}
							  }
							  """;

		CommitTranslationGuard.FindViolations("Example.cs", source).Should().NotBeEmpty();
	}

	[Fact]
	public void A_try_wrapped_commit_that_catches_the_concurrency_conflict_is_allowed()
	{
		const string source = """
							  class Example
							  {
							  	async System.Threading.Tasks.Task M(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
							  	{
							  		try {
							  			await transaction.CommitAsync();
							  		}
							  		catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) { throw; }
							  	}
							  }
							  """;

		CommitTranslationGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void A_bare_commit_outside_any_try_is_allowed()
	{
		const string source = """
							  class Example
							  {
							  	async System.Threading.Tasks.Task M(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) =>
							  		await transaction.CommitAsync();
							  }
							  """;

		CommitTranslationGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	private static string[] WorkSessionCommandPortFiles()
	{
		var srcRoot = Path.Combine(RepositoryPaths.SolutionRoot(), "src");
		var matches = Directory.EnumerateFiles(srcRoot, WorkSessionCommandPortFile, SearchOption.AllDirectories)
							   .Where(static file =>
								   file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is var segments
								   && !segments.Contains("bin") && !segments.Contains("obj"))
							   .ToArray();
		matches.Should().NotBeEmpty("the guard must find the work-session command port to be meaningful");

		return matches;
	}
}

internal static class CommitTranslationGuard
{
	public static IEnumerable<string> FindViolations(string fileName, string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();
		foreach (var commit in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsTransactionCommit)) {
			var enclosingTry = commit.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
			if (enclosingTry is not null && !CatchesConcurrencyConflict(enclosingTry)) {
				var line = commit.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
				yield return $"{Path.GetFileName(fileName)}:{line}: CommitAsync in a translating try must also catch DbUpdateConcurrencyException";
			}
		}
	}

	private static bool IsTransactionCommit(InvocationExpressionSyntax invocation) =>
		invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommitAsync" };

	private static bool CatchesConcurrencyConflict(TryStatementSyntax tryStatement) =>
		tryStatement.Catches.Any(static @catch =>
			@catch.Declaration?.Type is TypeSyntax type
			&& type.ToString().EndsWith("DbUpdateConcurrencyException", StringComparison.Ordinal));
}
