namespace JobTrack.Web;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

/// <summary>
///     Adds the PAT bearer scheme (ADR 0029) to the OpenAPI document's security schemes and requires
///     it on every <c>/api/*</c> operation, alongside the cookie scheme every operation already
///     accepts implicitly -- so the published contract documents the non-browser authentication path
///     the external API plan (§4.1) requires, not just the cookie flow visible from a browser session.
/// </summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
	internal const string SchemeName = "Bearer";

	public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
	{
		document.Components ??= new();
		document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
		document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme {
			Type = SecuritySchemeType.Http,
			Scheme = "bearer",
			Description = "Personal access token (ADR 0029): Authorization: Bearer <token>.",
		};
		var schemeReference = new OpenApiSecuritySchemeReference(SchemeName, document);

		foreach (var path in document.Paths.Values) {
			if (path.Operations is null) {
				continue;
			}

			foreach (var operation in path.Operations.Values) {
				operation.Security ??= [];
				operation.Security.Add(new() { [schemeReference] = [] });
			}
		}

		return Task.CompletedTask;
	}
}
