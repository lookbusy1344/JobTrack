namespace JobTrack.ArchitectureTests;

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestSupport;

/// <summary>
///     Remediation plan §2.5: neither a Razor Page <c>OnPost*</c> handler nor an external HTTP API
///     endpoint delegate may coordinate more than one <see cref="Application.IJobTrackClient" />
///     mutation as independent calls/transactions. Roslyn symbol analysis follows local helper calls
///     and aliases, so moving a mutation behind a method such as the former
///     <c>SaveWriteUpFirstAsync</c> cannot bypass the rule. The count is <em>branch-aware</em>: the
///     mutually-exclusive arms of a conditional (ternary, <c>if</c>/<c>else</c>, <c>switch</c>) count as
///     the single mutation that can actually run per request, not their sum -- a
///     <c>revoke ? RevokeRoleAsync() : AssignRoleAsync()</c> handler performs one mutation, not two.
/// </summary>
public sealed partial class OneHandlerOneMutationArchitectureTests
{
	private static readonly HashSet<string> MutationInterfaceNames = new(StringComparer.Ordinal) {
		"IInstallationCommands",
		"IEmployeeCommands",
		"IJobCommands",
		"IWorkCommands",
		"IScheduleCommands",
		"IRateCommands",
		"ITokenCommands",
		"IRequestCommands",
		"IAccountCredentialCommands",
	};

	/// <summary>
	///     The two historical mixed command/query facades. New members on a command facade default to
	///     mutation, so a future mutation cannot silently evade analysis; only these named reads are
	///     excluded. Authentication-attempt audit is the remediation plan's explicit host-composition
	///     exception and its complete facade is therefore outside <see cref="MutationInterfaceNames" />.
	/// </summary>
	private static readonly HashSet<string> ReadOnlyCommandFacadeMethods = new(StringComparer.Ordinal) {
		"IRequestCommands.GetMyRequestsAsync",
		"IRequestCommands.GetEligibleHoldingAreasAsync",
		"IRequestCommands.GetDetailAsync",
		"ITokenCommands.ListAsync",
		"ITokenCommands.TryAuthenticateAsync",
	};

	/// <summary>
	///     Empty by design. Its only former entry -- <c>AssignRole.OnPostAsync</c>'s
	///     <c>revoke ? RevokeRoleAsync() : AssignRoleAsync()</c> ternary -- was a counting artifact of the
	///     old flat analysis, not a genuine two-mutation handler; the branch-aware count now sees it as the
	///     one mutation it actually is. Kept as the explicit, reviewed escape hatch, but per remediation
	///     plan §2.5 an entry may only ever cover authentication-attempt audit or composition mechanics,
	///     never a business workflow.
	/// </summary>
	private static readonly string[] HandlerAllowlist = [];

	/// <summary>
	///     The facade interfaces documenting composites, paired with their concrete handlers. This
	///     complementary check keeps application handlers as one-port-call adapters.
	/// </summary>
	private static readonly (string InterfaceFile, string HandlerFile)[] CompositeCommandSources = [
		(Path.Combine("src", "JobTrack.Application", "IWorkCommands.cs"), Path.Combine("src", "JobTrack.Application", "WorkCommands.cs")),
		(
			Path.Combine("src", "JobTrack.Application", "IAccountCredentialCommands.cs"),
			Path.Combine("src", "JobTrack.Application", "AccountCredentialCommands.cs")),
	];

	[Fact]
	public void Mutation_analysis_follows_helper_indirection()
	{
		const string source = """
							  namespace GuardrailProof;

							  public interface IWorkCommands
							  {
							  	System.Threading.Tasks.Task FinishAsync();
							  }

							  public interface IJobCommands
							  {
							  	System.Threading.Tasks.Task EditAsync();
							  }

							  public interface IJobTrackClient
							  {
							  	IWorkCommands Work { get; }
							  	IJobCommands Jobs { get; }
							  }

							  public sealed class Page(IJobTrackClient client)
							  {
							  	public async System.Threading.Tasks.Task OnPostAsync()
							  	{
							  		await SaveWriteUpFirstAsync();
							  		await client.Work.FinishAsync();
							  	}

							  	private System.Threading.Tasks.Task SaveWriteUpFirstAsync() => client.Jobs.EditAsync();
							  }
							  """;

		CountMutationsInSyntheticHandler(source, "OnPostAsync").Should().Be(2);
	}

	[Fact]
	public void Mutation_analysis_resolves_client_aliases()
	{
		const string source = """
							  namespace GuardrailProof;

							  public interface IWorkCommands
							  {
							  	System.Threading.Tasks.Task StartAsync();
							  	System.Threading.Tasks.Task FinishAsync();
							  }

							  public interface IJobTrackClient
							  {
							  	IWorkCommands Work { get; }
							  }

							  public sealed class Page(IJobTrackClient client)
							  {
							  	public async System.Threading.Tasks.Task OnPostAsync()
							  	{
							  		var facade = client;
							  		await facade.Work.StartAsync();
							  		await facade.Work.FinishAsync();
							  	}
							  }
							  """;

		CountMutationsInSyntheticHandler(source, "OnPostAsync").Should().Be(2);
	}

	[Fact]
	public void Mutation_analysis_takes_the_max_across_a_conditional_expressions_arms()
	{
		const string source = """
							  namespace GuardrailProof;

							  public interface IEmployeeCommands
							  {
							  	System.Threading.Tasks.Task<int> AssignRoleAsync();
							  	System.Threading.Tasks.Task<int> RevokeRoleAsync();
							  }

							  public interface IJobTrackClient
							  {
							  	IEmployeeCommands Employees { get; }
							  }

							  public sealed class Page(IJobTrackClient client)
							  {
							  	public async System.Threading.Tasks.Task OnPostAsync(bool revoke)
							  	{
							  		var result = revoke
							  			? await client.Employees.RevokeRoleAsync()
							  			: await client.Employees.AssignRoleAsync();
							  	}
							  }
							  """;

		CountMutationsInSyntheticHandler(source, "OnPostAsync").Should().Be(1);
	}

	[Fact]
	public void Mutation_analysis_takes_the_max_across_if_else_branches()
	{
		const string source = """
							  namespace GuardrailProof;

							  public interface IEmployeeCommands
							  {
							  	System.Threading.Tasks.Task AssignRoleAsync();
							  	System.Threading.Tasks.Task RevokeRoleAsync();
							  }

							  public interface IJobTrackClient
							  {
							  	IEmployeeCommands Employees { get; }
							  }

							  public sealed class Page(IJobTrackClient client)
							  {
							  	public async System.Threading.Tasks.Task OnPostAsync(bool revoke)
							  	{
							  		if (revoke) {
							  			await client.Employees.RevokeRoleAsync();
							  		} else {
							  			await client.Employees.AssignRoleAsync();
							  		}
							  	}
							  }
							  """;

		CountMutationsInSyntheticHandler(source, "OnPostAsync").Should().Be(1);
	}

	[Fact]
	public void Mutation_analysis_still_sums_sequential_mutations_outside_a_branch()
	{
		const string source = """
							  namespace GuardrailProof;

							  public interface IEmployeeCommands
							  {
							  	System.Threading.Tasks.Task AssignRoleAsync();
							  	System.Threading.Tasks.Task RevokeRoleAsync();
							  }

							  public interface IJobTrackClient
							  {
							  	IEmployeeCommands Employees { get; }
							  }

							  public sealed class Page(IJobTrackClient client)
							  {
							  	public async System.Threading.Tasks.Task OnPostAsync(bool revoke)
							  	{
							  		if (revoke) {
							  			await client.Employees.RevokeRoleAsync();
							  		}

							  		await client.Employees.AssignRoleAsync();
							  	}
							  }
							  """;

		CountMutationsInSyntheticHandler(source, "OnPostAsync").Should().Be(2);
	}

	[Fact]
	public void Every_razor_page_post_handler_invokes_at_most_one_mutation()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		var compilation = CreateWebCompilation(solutionRoot);

		var violations = compilation.SyntaxTrees
			.Where(tree => tree.FilePath.EndsWith(".cshtml.cs", StringComparison.Ordinal))
			.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
				.Where(method => method.Identifier.ValueText.StartsWith("OnPost", StringComparison.Ordinal))
				.Select(method => AnalyzeHandler(compilation, tree, method, solutionRoot)))
			.Where(result => !HandlerAllowlist.Contains($"{result.Path}:{result.Name}", StringComparer.Ordinal))
			.Where(result => result.MutationCount > 1)
			.Select(result => $"{result.Path}:{result.Name} ({result.MutationCount} mutations)")
			.ToList();

		violations.Should().BeEmpty(
			"a Razor Page handler coordinating more than one IJobTrackClient mutation call belongs as one atomic " +
			$"library command instead (remediation plan §2.5). Violations: {string.Join(", ", violations)}");
	}

	[Fact]
	public void Every_external_api_endpoint_delegate_invokes_at_most_one_mutation()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();
		var compilation = CreateWebCompilation(solutionRoot);

		var violations = compilation.SyntaxTrees
			.Where(tree => tree.FilePath.EndsWith("JobTrackApi.cs", StringComparison.Ordinal))
			.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
				.Where(IsApiEndpointDelegate)
				.Select(method => AnalyzeHandler(compilation, tree, method, solutionRoot)))
			.Where(result => result.MutationCount > 1)
			.Select(result => $"{result.Path}:{result.Name} ({result.MutationCount} mutations)")
			.ToList();

		violations.Should().BeEmpty(
			"an external API endpoint delegate coordinating more than one IJobTrackClient mutation call belongs as " +
			"one atomic library command instead (remediation plan §2.5)");
	}

	[GeneratedRegex(@"Task<[^(]+?>\s+(\w+Async)\s*\(", RegexOptions.Multiline)]
	private static partial Regex InterfaceMethodSignature();

	[GeneratedRegex(@"\b_?\w*[Pp]ort\.\w+\(")]
	private static partial Regex PortCallPattern();

	[Fact]
	public void Every_command_method_documented_as_a_composite_calls_exactly_one_port_method()
	{
		var solutionRoot = RepositoryPaths.SolutionRoot();

		var violations = new List<string>();
		foreach (var (interfaceFile, handlerFile) in CompositeCommandSources) {
			var compositeMethodNames = ExtractCompositeDocumentedMethodNames(File.ReadAllText(Path.Combine(solutionRoot, interfaceFile)));
			var handlerContent = File.ReadAllText(Path.Combine(solutionRoot, handlerFile));

			foreach (var methodName in compositeMethodNames) {
#pragma warning disable SYSLIB1045
				var signature = new Regex(@"Task<[^(]+?>\s+" + Regex.Escape(methodName) + @"\s*\(", RegexOptions.Multiline);
#pragma warning restore SYSLIB1045
				var (_, body) = ExtractMethods(handlerContent, signature).SingleOrDefault();
				if (body is null) {
					violations.Add($"{handlerFile}:{methodName} (not found)");
					continue;
				}

				var portCallCount = PortCallPattern().Count(body);
				if (portCallCount != 1) {
					violations.Add($"{handlerFile}:{methodName} ({portCallCount} port calls)");
				}
			}
		}

		violations.Should().BeEmpty(
			"a command method documented as an atomic composite must delegate to exactly one persistence-port call");
	}

	private static (string Path, string Name, int MutationCount) AnalyzeHandler(
		CSharpCompilation compilation,
		SyntaxTree tree,
		MethodDeclarationSyntax method,
		string solutionRoot)
	{
		var symbol = compilation.GetSemanticModel(tree).GetDeclaredSymbol(method)
					 ?? throw new InvalidOperationException($"Could not resolve handler {method.Identifier.ValueText}.");
		var mutationCount = CountMutations(compilation, symbol, []);

		return (Path.GetRelativePath(solutionRoot, tree.FilePath), method.Identifier.ValueText, mutationCount);
	}

	private static int CountMutations(
		CSharpCompilation compilation,
		IMethodSymbol method,
		ImmutableHashSet<IMethodSymbol> callStack)
	{
		var canonicalMethod = method.OriginalDefinition;
		if (callStack.Contains(canonicalMethod, SymbolEqualityComparer.Default)) {
			return 0;
		}

		var nextCallStack = callStack.Add(canonicalMethod);
		var mutationCount = 0;
		foreach (var syntaxReference in canonicalMethod.DeclaringSyntaxReferences) {
			if (syntaxReference.GetSyntax() is not MethodDeclarationSyntax declaration) {
				continue;
			}

			var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
			mutationCount += CountNode(compilation, semanticModel, nextCallStack, declaration);
		}

		return mutationCount;
	}

	/// <summary>
	///     The branch-aware mutation count of one syntax subtree: sequential statements sum, but the
	///     mutually-exclusive arms of a conditional take the <see cref="Math.Max(int, int)" /> instead,
	///     since only one arm executes per request. Invocations of a local helper recurse into the
	///     helper (following indirection); a local function's own body is analyzed only where it is
	///     invoked, never inline at its declaration.
	/// </summary>
	private static int CountNode(
		CSharpCompilation compilation,
		SemanticModel semanticModel,
		ImmutableHashSet<IMethodSymbol> callStack,
		SyntaxNode node) =>
		node switch {
			InvocationExpressionSyntax invocation => CountInvocation(compilation, semanticModel, callStack, invocation),
			ConditionalExpressionSyntax conditional =>
				CountNode(compilation, semanticModel, callStack, conditional.Condition)
				+ Math.Max(
					CountNode(compilation, semanticModel, callStack, conditional.WhenTrue),
					CountNode(compilation, semanticModel, callStack, conditional.WhenFalse)),
			IfStatementSyntax ifStatement =>
				CountNode(compilation, semanticModel, callStack, ifStatement.Condition)
				+ Math.Max(
					CountNode(compilation, semanticModel, callStack, ifStatement.Statement),
					ifStatement.Else is null ? 0 : CountNode(compilation, semanticModel, callStack, ifStatement.Else.Statement)),
			SwitchStatementSyntax switchStatement =>
				CountNode(compilation, semanticModel, callStack, switchStatement.Expression)
				+ (switchStatement.Sections.Count == 0
					? 0
					: switchStatement.Sections.Max(section =>
						section.Statements.Sum(statement => CountNode(compilation, semanticModel, callStack, statement)))),
			SwitchExpressionSyntax switchExpression =>
				CountNode(compilation, semanticModel, callStack, switchExpression.GoverningExpression)
				+ (switchExpression.Arms.Count == 0
					? 0
					: switchExpression.Arms.Max(arm => CountNode(compilation, semanticModel, callStack, arm.Expression))),
			LocalFunctionStatementSyntax => 0,
			_ => node.ChildNodes().Sum(child => CountNode(compilation, semanticModel, callStack, child)),
		};

	/// <summary>
	///     Counts a single invocation: one for a mutation-facade call, otherwise the branch-aware count
	///     of the invoked local helper's body, plus any mutations nested in the invocation's own
	///     arguments or receiver chain.
	/// </summary>
	private static int CountInvocation(
		CSharpCompilation compilation,
		SemanticModel semanticModel,
		ImmutableHashSet<IMethodSymbol> callStack,
		InvocationExpressionSyntax invocation)
	{
		var nested = invocation.ChildNodes().Sum(child => CountNode(compilation, semanticModel, callStack, child));

		var symbolInfo = semanticModel.GetSymbolInfo(invocation);
		var invokedMethod = symbolInfo.Symbol as IMethodSymbol
							?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
		if (invokedMethod is null) {
			return nested;
		}

		var methodKey = $"{invokedMethod.ContainingType.Name}.{invokedMethod.Name}";
		if (MutationInterfaceNames.Contains(invokedMethod.ContainingType.Name)
			&& !ReadOnlyCommandFacadeMethods.Contains(methodKey)) {
			return nested + 1;
		}

		if (invokedMethod.DeclaringSyntaxReferences.Length > 0) {
			return nested + CountMutations(compilation, invokedMethod, callStack);
		}

		return nested;
	}

	private static int CountMutationsInSyntheticHandler(string source, string methodName)
	{
		var tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
		var compilation = CreateCompilation([tree]);
		var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
			.Single(candidate => candidate.Identifier.ValueText == methodName);
		var symbol = compilation.GetSemanticModel(tree).GetDeclaredSymbol(method)
					 ?? throw new InvalidOperationException($"Could not resolve synthetic handler {methodName}.");

		return CountMutations(compilation, symbol, []);
	}

	private static CSharpCompilation CreateWebCompilation(string solutionRoot)
	{
		var webRoot = Path.Combine(solutionRoot, "src", "JobTrack.Web");
		var sourceTrees = Directory.EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.Combine("obj", ""), StringComparison.Ordinal)
						   && !path.Contains(Path.Combine("bin", ""), StringComparison.Ordinal))
			.Select(path => CSharpSyntaxTree.ParseText(
				File.ReadAllText(path),
				CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
				path))
			.Prepend(CSharpSyntaxTree.ParseText(
				"""
				global using System;
				global using System.Collections.Generic;
				global using System.IO;
				global using System.Linq;
				global using System.Net.Http;
				global using System.Threading;
				global using System.Threading.Tasks;
				""",
				CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)));

		return CreateCompilation(sourceTrees);
	}

	private static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> syntaxTrees) =>
		CSharpCompilation.Create(
			"OneHandlerOneMutationAnalysis",
			syntaxTrees,
			CreateMetadataReferences(),
			new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

	private static IEnumerable<MetadataReference> CreateMetadataReferences()
	{
		var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
			?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
		var copiedDependencies = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
			.Where(path => !Path.GetFileName(path).Equals("JobTrack.Web.dll", StringComparison.Ordinal));

		return trustedPlatformAssemblies
			.Concat(copiedDependencies)
			.Distinct(StringComparer.Ordinal)
			.Select(path => MetadataReference.CreateFromFile(path));
	}

	private static bool IsApiEndpointDelegate(MethodDeclarationSyntax method) =>
		method.Modifiers.Any(SyntaxKind.PrivateKeyword)
		&& method.Modifiers.Any(SyntaxKind.StaticKeyword)
		&& method.ReturnType.ToString().Contains("Task<IResult>", StringComparison.Ordinal);

	private static List<string> ExtractCompositeDocumentedMethodNames(string interfaceContent)
	{
		var names = new List<string>();
		var sawCompositeInCurrentDocBlock = false;
		foreach (var line in interfaceContent.Split('\n')) {
			var trimmed = line.TrimStart();
			if (trimmed.StartsWith("///", StringComparison.Ordinal)) {
				if (trimmed.Contains("composite", StringComparison.OrdinalIgnoreCase)) {
					sawCompositeInCurrentDocBlock = true;
				}

				continue;
			}

			if (string.IsNullOrWhiteSpace(trimmed)) {
				continue;
			}

			var signatureMatch = InterfaceMethodSignature().Match(trimmed);
			if (signatureMatch.Success && sawCompositeInCurrentDocBlock) {
				names.Add(signatureMatch.Groups[1].Value);
			}

			sawCompositeInCurrentDocBlock = false;
		}

		return names;
	}

	private static IEnumerable<(string Name, string Body)> ExtractMethods(string content, Regex signature)
	{
		foreach (Match match in signature.Matches(content)) {
			var braceStart = content.IndexOf('{', match.Index);
			if (braceStart < 0) {
				continue;
			}

			var depth = 0;
			var index = braceStart;
			for (; index < content.Length; index++) {
				if (content[index] == '{') {
					depth++;
				} else if (content[index] == '}') {
					depth--;
					if (depth == 0) {
						break;
					}
				}
			}

			yield return (match.Groups[1].Value, content[braceStart..(index + 1)]);
		}
	}
}
