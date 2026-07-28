namespace JobTrack.ArchitectureTests;

using System.Collections.Frozen;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     §2.6 of the 2026-07-28 fresh-eyes review: house style (CLAUDE.md) forbids
///     mutable arrays and collections as <c>static readonly</c> constant tables -- <c>readonly</c>
///     freezes only the field's own reference, not its elements, so mutating a shared allowlist still
///     compiles. Scans the tracked <c>.cs</c> sources under <c>src</c>, <c>tests</c>, and
///     <c>samples</c> with Roslyn without representing the forbidden shape internally.
/// </summary>
public sealed class MutableConstantTableArchitectureTests
{
	[Fact]
	public void Repository_sources_have_no_mutable_static_readonly_constant_table()
	{
		var violations = SourceFiles()
			.SelectMany(static file => MutableConstantTableGuard.FindViolations(file, File.ReadAllText(file)))
			.ToArray();

		violations.Should().BeEmpty(
			"a mutable static readonly array or collection leaves a constant table's elements writable -- use " +
			"FrozenSet<T>/FrozenDictionary<TKey, TValue> for membership, static ReadOnlySpan<T> for an ordered " +
			"constant, or IReadOnlyList<T> only where an EF expression tree must capture it:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Theory]
	[InlineData("private static readonly string[] Names = [\"a\", \"b\"];")]
	[InlineData("private static readonly int[] Numbers = [1, 2, 3];")]
	[InlineData("internal static readonly MyType[] Items = [];")]
	[InlineData("public static readonly (string, string)[] Pairs = [];")]
	[InlineData("private static readonly HashSet<string> Names = [\"a\"];")]
	[InlineData("private static readonly Dictionary<string, int> Values = new() { [\"a\"] = 1 };")]
	[InlineData("private static readonly List<string> OrderedNames = [\"a\"];")]
	public void Mutable_static_readonly_constant_table_is_a_violation(string field)
	{
		var source = $"class Example {{ {field} }}";

		MutableConstantTableGuard.FindViolations("Example.cs", source).Should().NotBeEmpty();
	}

	[Theory]
	[InlineData("private static readonly System.Collections.Frozen.FrozenSet<string> Names = System.Collections.Frozen.FrozenSet<string>.Empty;")]
	[InlineData("private static System.ReadOnlySpan<int> Numbers => [1, 2, 3];")]
	[InlineData("private readonly string[] instanceArray = [];")] // instance field, not static
	[InlineData("private static string[] mutableTable = [];")] // deliberately mutable (no readonly) — not this rule's concern
	public void Non_array_or_non_static_readonly_fields_are_not_violations(string field)
	{
		var source = $"class Example {{ {field} }}";

		MutableConstantTableGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void Private_empty_dictionary_used_only_as_an_allocation_free_backing_store_is_not_a_constant_table()
	{
		const string Field = "private static readonly Dictionary<TKey, TValue> Empty = [];";
		var path = Path.Combine("src", "JobTrack.Abstractions", "EquatableDictionary.cs");
		var source = $"class EquatableDictionary<TKey, TValue> {{ {Field} }}";

		MutableConstantTableGuard.FindViolations(path, source).Should().BeEmpty();
	}

	private static IEnumerable<string> SourceFiles()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		foreach (var top in (string[])["src", "tests", "samples"]) {
			var directory = Path.Combine(solutionRoot, top);
			foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
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

internal static class MutableConstantTableGuard
{
	private static readonly FrozenSet<string> MutableCollectionTypeNames = FrozenSet.ToFrozenSet(
		[
			"Collection",
			"Dictionary",
			"HashSet",
			"LinkedList",
			"List",
			"ObservableCollection",
			"Queue",
			"SortedDictionary",
			"SortedSet",
			"Stack",
		],
		StringComparer.Ordinal);

	public static IEnumerable<string> FindViolations(string fileName, string source)
	{
		var root = CSharpSyntaxTree.ParseText(source).GetRoot();
		return root.DescendantNodes()
			.OfType<FieldDeclarationSyntax>()
			.Where(IsStaticReadonly)
			.Where(IsMutableTableType)
			.Where(field => !IsReviewedPrivateBackingStore(fileName, field))
			.Select(field => Describe(fileName, field.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
	}

	private static bool IsStaticReadonly(FieldDeclarationSyntax field) =>
		field.Modifiers.Any(SyntaxKind.StaticKeyword) && field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);

	private static bool IsMutableTableType(FieldDeclarationSyntax field)
	{
		if (field.Declaration.Type is ArrayTypeSyntax) {
			return true;
		}

		var genericType = field.Declaration.Type
			.DescendantNodesAndSelf()
			.OfType<GenericNameSyntax>()
			.LastOrDefault();
		return genericType is not null && MutableCollectionTypeNames.Contains(genericType.Identifier.ValueText);
	}

	private static bool IsReviewedPrivateBackingStore(string fileName, FieldDeclarationSyntax field) =>
		fileName.EndsWith(
			Path.Combine("src", "JobTrack.Abstractions", "EquatableDictionary.cs"),
			StringComparison.Ordinal)
		&& field.Declaration.Variables is [{ Identifier.ValueText: "Empty" }];

	private static string Describe(string fileName, int line) =>
		$"{Path.GetFileName(fileName)}:{line}: mutable static readonly constant table";
}
