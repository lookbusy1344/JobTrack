namespace JobTrack.ArchitectureTests;

using System.Collections.Frozen;
using System.Collections.Immutable;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Remediation plan §2.4 step 5: every public <c>IJobQueries</c> member must authenticate the
///     caller-supplied <see cref="Application.CommandContext.Actor" /> before returning data, so a
///     future read cannot silently reuse the caller-supplied actor without checking current account
///     state. Rather than trusting each method's own judgement, this walks <c>JobQueries.cs</c>'s
///     Roslyn syntax tree (following local private-helper indirection, since every public member here
///     delegates to a <c>...CoreAsync</c> twin whose body is a lambda passed to
///     <c>JobTrackOperation.TraceAsync</c>) and asserts each public member's declaration invokes at
///     least one of <see cref="AdmissionMethodNames" /> -- the shared
///     <c>EnsureActorMayBrowseJobDataAsync</c> gate, a direct <c>IEmployeeQueryPort.GetActorRolesAsync</c>
///     reload, or one of the other ports that already load the actor's current roles as part of
///     answering the query (session/schedule/rate ports).
/// </summary>
public sealed class JobQueriesAdmissionArchitectureTests
{
	/// <summary>
	///     Method names whose presence anywhere in a public member's call graph (through local private
	///     helpers) counts as declaring an admission category. Every one of these either reloads the
	///     actor's account state and roles fresh from a port, or is the shared gate that itself does so.
	/// </summary>
	private static readonly FrozenSet<string> AdmissionMethodNames = FrozenSet.ToFrozenSet([
		"EnsureActorMayBrowseJobDataAsync",
		"GetActorRolesAsync",
		"GetSessionsAsync",
		"GetActiveSessionsAsync",
		"GetManageCapabilitiesAsync",
		"GetScheduleAsync",
		"GetRatesAsync",
	], StringComparer.Ordinal);

	[Fact]
	public void Every_public_IJobQueries_member_declares_an_admission_category()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		var applicationRoot = Path.Combine(solutionRoot, "src", "JobTrack.Application");
		var compilation = CreateApplicationCompilation(applicationRoot);

		var jobQueriesTree = compilation.SyntaxTrees.Single(tree => Path.GetFileName(tree.FilePath) == "JobQueries.cs");
		var semanticModel = compilation.GetSemanticModel(jobQueriesTree);

		var classDeclaration = jobQueriesTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
			.Single(c => c.Identifier.ValueText == "JobQueries");

		var publicMembers = classDeclaration.Members.OfType<MethodDeclarationSyntax>()
			.Where(method => method.Modifiers.Any(SyntaxKind.PublicKeyword))
			.ToList();

		publicMembers.Should().NotBeEmpty("the scan must actually find JobQueries's public interface members");

		var violations = publicMembers
			.Where(method => !DeclaresAdmission(compilation, semanticModel, method, []))
			.Select(method => method.Identifier.ValueText)
			.ToList();

		violations.Should().BeEmpty(
			"every public IJobQueries member must authenticate the actor before returning data (remediation plan " +
			$"§2.4) -- add a call to EnsureActorMayBrowseJobDataAsync (or an equivalent actor-reloading port call) " +
			$"to: {string.Join(", ", violations)}");
	}

	private static bool DeclaresAdmission(
		CSharpCompilation compilation, SemanticModel semanticModel, SyntaxNode node, ImmutableHashSet<IMethodSymbol> visited)
	{
		foreach (var invocation in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()) {
			var name = invocation.Expression switch {
				MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
				IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
				_ => null,
			};

			if (name is not null && AdmissionMethodNames.Contains(name)) {
				return true;
			}

			var invokedMethod = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
			if (invokedMethod is null || visited.Contains(invokedMethod.OriginalDefinition, SymbolEqualityComparer.Default)) {
				continue;
			}

			foreach (var syntaxReference in invokedMethod.OriginalDefinition.DeclaringSyntaxReferences) {
				if (syntaxReference.GetSyntax() is not MethodDeclarationSyntax declaration) {
					continue;
				}

				var declarationSemanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
				if (DeclaresAdmission(
						compilation, declarationSemanticModel, declaration, visited.Add(invokedMethod.OriginalDefinition))) {
					return true;
				}
			}
		}

		return false;
	}

	private static CSharpCompilation CreateApplicationCompilation(string applicationRoot)
	{
		var sourceTrees = Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.Combine("obj", ""), StringComparison.Ordinal)
						   && !path.Contains(Path.Combine("bin", ""), StringComparison.Ordinal))
			.Select(path => CSharpSyntaxTree.ParseText(
				File.ReadAllText(path), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), path))
			.Prepend(CSharpSyntaxTree.ParseText(
				"""
				global using System;
				global using System.Collections.Generic;
				global using System.Linq;
				global using System.Threading;
				global using System.Threading.Tasks;
				""",
				CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)));

		return CSharpCompilation.Create(
			"JobQueriesAdmissionAnalysis",
			sourceTrees,
			CreateMetadataReferences(),
			new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
	}

	private static IEnumerable<MetadataReference> CreateMetadataReferences()
	{
		var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
			?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
		var copiedDependencies = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll");

		return trustedPlatformAssemblies
			.Concat(copiedDependencies)
			.Distinct(StringComparer.Ordinal)
			.Select(path => MetadataReference.CreateFromFile(path));
	}
}
