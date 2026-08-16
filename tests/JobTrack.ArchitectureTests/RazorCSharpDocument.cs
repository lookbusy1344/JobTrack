namespace JobTrack.ArchitectureTests;

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
///     Parses Razor with the compiler shipped by the selected .NET SDK, then exposes only syntax
///     nodes which map back to authored C# in the original document. Generated rendering machinery,
///     markup, HTML comments and Razor comments consequently cannot become architecture violations.
/// </summary>
internal sealed class RazorCSharpDocument
{
	private static readonly RazorProjectEngine ProjectEngine = RazorProjectEngine.Create(
		RazorConfiguration.Default,
		RazorProjectFileSystem.Empty);

	private static readonly MethodInfo GetCSharpDocumentMethod =
		typeof(RazorCodeDocument).GetMethod("GetCSharpDocument", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("The selected Razor compiler does not expose its generated C# document.");

	private readonly Microsoft.AspNetCore.Razor.Language.RazorCSharpDocument generated;

	private RazorCSharpDocument(Microsoft.AspNetCore.Razor.Language.RazorCSharpDocument generated)
	{
		this.generated = generated;
		Root = CSharpSyntaxTree.ParseText(
			generated.Text.ToString(),
			CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)).GetRoot();
	}

	public SyntaxNode Root { get; }

	public static RazorCSharpDocument Parse(string fileName, string source)
	{
		var sourceDocument = RazorSourceDocument.Create(source, fileName);
		var codeDocument = ProjectEngine.Process(
			sourceDocument,
			RazorFileKind.Legacy,
			ImmutableArray<RazorSourceDocument>.Empty,
			tagHelpers: null);
		var generated = GetCSharpDocumentMethod.Invoke(codeDocument, null) as Microsoft.AspNetCore.Razor.Language.RazorCSharpDocument
			?? throw new InvalidOperationException("The Razor compiler did not produce a generated C# document.");

		return new(generated);
	}

	public int? OriginalLine(SyntaxNode node)
	{
		var generatedPosition = node.SpanStart;
		foreach (var mapping in generated.SourceMappingsSortedByGenerated) {
			var generatedSpan = mapping.GeneratedSpan;
			if (generatedPosition < generatedSpan.AbsoluteIndex
				|| generatedPosition >= generatedSpan.AbsoluteIndex + generatedSpan.Length) {
				continue;
			}

			var originalPosition = mapping.OriginalSpan.AbsoluteIndex + generatedPosition - generatedSpan.AbsoluteIndex;
			return generated.CodeDocument.Source.Text.Lines.GetLineFromPosition(originalPosition).LineNumber + 1;
		}

		return null;
	}
}
