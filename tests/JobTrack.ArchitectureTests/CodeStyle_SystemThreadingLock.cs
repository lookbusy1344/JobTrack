namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
///     Architecture guard requiring every C# <c>lock</c> statement to target a value whose
///     compile-time type is precisely <see cref="System.Threading.Lock" />. This preserves the
///     compiler's optimized lock lowering instead of falling back to <see cref="Monitor" />.
///     Scans the tracked <c>.cs</c> and <c>.cshtml</c> sources under <c>src</c>, <c>tests</c>, and
///     <c>samples</c>.
/// </summary>
public sealed class CodeStyle_SystemThreadingLock
{
	[Fact]
	public void Repository_lock_statements_use_System_Threading_Lock()
	{
		var violations = RepositorySourceFiles.CSharpAndRazor()
											  .SelectMany(static file => SystemThreadingLockGuard.FindViolations(file, File.ReadAllText(file)))
											  .ToArray();

		violations.Should().BeEmpty(
			"lock statements should use the optimized System.Threading.Lock implementation:{0}{1}",
			Environment.NewLine,
			string.Join(Environment.NewLine, violations));
	}

	[Theory]
	[InlineData("private readonly object gate = new();")]
	[InlineData("private readonly System.Text.StringBuilder gate = new();")]
	public void Other_lock_target_types_are_violations(string declaration)
	{
		var source = $"class Example {{ {declaration} void M() {{ lock (gate) {{ }} }} }}";

		SystemThreadingLockGuard.FindViolations("Example.cs", source).Should().ContainSingle();
	}

	[Theory]
	[InlineData("private readonly System.Threading.Lock gate = new();")]
	[InlineData("private readonly Lock gate = new();")]
	public void System_Threading_Lock_targets_are_allowed(string declaration)
	{
		var source = $"class Example {{ {declaration} void M() {{ lock (gate) {{ }} }} }}";

		SystemThreadingLockGuard.FindViolations("Example.cs", source).Should().BeEmpty();
	}

	[Fact]
	public void Razor_lock_target_types_are_checked()
	{
		const string source = "@functions { private readonly object gate = new(); void M() { lock (gate) { } } }";

		SystemThreadingLockGuard.FindViolations("Example.cshtml", source).Should().ContainSingle();
	}
}

internal static class SystemThreadingLockGuard
{
	private const string SystemThreadingLockType = "global::System.Threading.Lock";

	private static readonly IReadOnlyList<MetadataReference> PlatformReferences =
		((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
		 ?? throw new InvalidOperationException("The runtime did not expose its trusted platform assemblies."))
		.Split(Path.PathSeparator)
		.Distinct(StringComparer.Ordinal)
		.Select(static path => MetadataReference.CreateFromFile(path))
		.ToArray();

	private static readonly SyntaxTree ImplicitUsings = CSharpSyntaxTree.ParseText(
		"global using System.Threading;",
		CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));

	public static IEnumerable<string> FindViolations(string fileName, string source)
	{
		RazorCSharpDocument? razorDocument = null;
		var root = fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
			? (razorDocument = RazorCSharpDocument.Parse(fileName, source)).Root
			: CSharpSyntaxTree.ParseText(
				source,
				CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
				fileName).GetRoot();
		var compilation = CSharpCompilation.Create(
			"LockGuard",
			[root.SyntaxTree, ImplicitUsings],
			PlatformReferences);
		var semanticModel = compilation.GetSemanticModel(root.SyntaxTree);

		return root.DescendantNodes()
				   .OfType<LockStatementSyntax>()
				   .Where(statement => semanticModel.GetTypeInfo(statement.Expression).Type?.ToDisplayString(
					   SymbolDisplayFormat.FullyQualifiedFormat) != SystemThreadingLockType)
				   .Select(statement => Describe(fileName, OriginalLine(statement, razorDocument)))
				   .ToArray();
	}

	private static int OriginalLine(LockStatementSyntax statement, RazorCSharpDocument? razorDocument) =>
		razorDocument?.OriginalLine(statement)
		?? statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

	private static string Describe(string fileName, int line) =>
		$"{Path.GetFileName(fileName)}:{line}: lock target is not System.Threading.Lock";
}
