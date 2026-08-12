namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Remediation plan §3.2 step 3's coverage guard. ADR 0058's auto-acknowledgement is a side effect
///     of a leaf's achievement transition, so it is correct exactly as long as every transition goes
///     through <c>LeafAchievementTransition.ApplyAsync</c>. The create-and-begin-work composite was the
///     defect this guard exists to prevent recurring: it reproduced the mutation and its audit event
///     locally and silently dropped the acknowledgement. Rather than maintain a list of the call sites
///     that must remember to acknowledge, this asserts there is nowhere else the mutation can happen.
///     <para>
///         Object-initializer assignments are deliberately not violations: creating a new
///         <c>leaf_work</c> row at its initial achievement is not a transition of an existing one, and
///         has no request to acknowledge on behalf of.
///     </para>
/// </summary>
public sealed class LeafAchievementTransitionArchitectureTests
{
	private const string TransitionType = "LeafAchievementTransition";
	private const string AchievementMember = "Achievement";

	[Fact]
	public void Only_the_shared_transition_helper_reassigns_a_tracked_leafs_achievement()
	{
		var violations = PersistenceSourceFiles()
						 .Where(static file => Path.GetFileNameWithoutExtension(file) != TransitionType)
						 .SelectMany(static file => FindAchievementReassignments(file, File.ReadAllText(file)))
						 .ToArray();

		violations.Should().BeEmpty(
			"every leaf achievement transition should go through {0}.ApplyAsync so ADR 0058's "
			+ "auto-acknowledgement cannot be dropped by a new composite:{1}{2}",
			TransitionType,
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void The_shared_transition_helper_is_the_only_caller_of_the_acknowledgement_helper()
	{
		var callers = PersistenceSourceFiles()
					  .Where(static file => File.ReadAllText(file).Contains("AcknowledgeIfNeededAsync(", StringComparison.Ordinal))
					  .Select(static file => Path.GetFileNameWithoutExtension(file))
					  .Order(StringComparer.Ordinal)
					  .ToArray();

		callers.Should().BeEquivalentTo(TransitionType, "RequesterRequestAutoAcknowledgement");
	}

	internal static IEnumerable<string> FindAchievementReassignments(string file, string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();

		foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>()) {
			if (assignment.Parent is InitializerExpressionSyntax) {
				continue;
			}

			if (assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: AchievementMember } member) {
				yield return $"{Path.GetFileName(file)}: {member}";
			}
		}
	}

	private static IEnumerable<string> PersistenceSourceFiles()
	{
		var source = Path.Combine(RepositoryPaths.SolutionRoot(), "src");
		foreach (var project in (string[])[
					 "JobTrack.Persistence.Shared", "JobTrack.Persistence.PostgreSql", "JobTrack.Persistence.Sqlite",
				 ]) {
			foreach (var file in Directory.EnumerateFiles(Path.Combine(source, project), "*.cs", SearchOption.AllDirectories)
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
