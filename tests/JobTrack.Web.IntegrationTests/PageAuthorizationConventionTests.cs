namespace JobTrack.Web.IntegrationTests;

using System.Collections.Frozen;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TestSupport;
using Program = Program;

/// <summary>
///     Security-audit finding 5: Razor Pages are anonymous unless a page opts in, so a page added
///     without <c>[Authorize]</c> would ship publicly readable. <see cref="Web.Program" />'s
///     <c>AuthorizeFolder("/")</c> convention inverts that default at the framework level, with an
///     explicit <c>AllowAnonymousToPage</c> allowlist for the sign-in sequence. These tests assert on
///     the convention's own contribution rather than on any page's attribute, so they fail if the
///     convention is dropped even while every page still carries its attribute.
/// </summary>
public sealed class PageAuthorizationConventionTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private static readonly FrozenSet<string> ExpectedAnonymousPages = FrozenSet.ToFrozenSet([
		"/Index",
		"/Error",
		"/Account/Login",
		"/Account/LoginTwoFactor",
		"/Account/Logout",
		"/Account/AccessDenied",
	], StringComparer.Ordinal);

	private readonly SqliteDatabaseFixture database = new();
	private TestWebApplicationFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		factory = new(database.ConnectionString);
		// Force the host to build so the action descriptors below are populated.
		_ = factory.Services;
	}

	public async Task DisposeAsync()
	{
		Dispose();
		await database.DisposeAsync();
	}

	public void Dispose() => factory.Dispose();

	/// <summary>
	///     Every page model declares a named policy, so a policy-less <see cref="IAuthorizeData" /> on a
	///     page endpoint can only have come from the folder convention. Asserting on that specific
	///     contribution keeps this test failing if the convention is removed, even though every page
	///     would still carry its own attribute.
	/// </summary>
	[Fact]
	public void Every_page_is_closed_by_the_folder_convention_independently_of_its_own_attribute()
	{
		var uncovered = PageEndpoints()
			.Where(page => !page.Endpoint.Metadata.OfType<IAuthorizeData>().Any(IsConventionApplied))
			.Select(page => page.ViewEnginePath)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		uncovered.Should().BeEmpty("AuthorizeFolder(\"/\") must close every page regardless of its own attribute");
	}

	[Fact]
	public void Exactly_the_expected_pages_are_marked_anonymous()
	{
		var anonymous = PageEndpoints()
			.Where(page => page.Endpoint.Metadata.OfType<IAllowAnonymous>().Any())
			.Select(page => page.ViewEnginePath)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		anonymous.Should().BeEquivalentTo(ExpectedAnonymousPages);
	}

	// The folder convention contributes a bare AuthorizeAttribute -- no policy, no roles, no scheme --
	// which every page's own [Authorize(Policy = ...)] attribute never is.
	private static bool IsConventionApplied(IAuthorizeData authorizeData) =>
		string.IsNullOrEmpty(authorizeData.Policy)
		&& string.IsNullOrEmpty(authorizeData.Roles)
		&& string.IsNullOrEmpty(authorizeData.AuthenticationSchemes);

	private IEnumerable<(string ViewEnginePath, Endpoint Endpoint)> PageEndpoints() =>
		factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
			.Select(endpoint => (Descriptor: endpoint.Metadata.GetMetadata<PageActionDescriptor>(), Endpoint: endpoint))
			.Where(entry => entry.Descriptor is not null)
			.Select(entry => (entry.Descriptor!.ViewEnginePath, entry.Endpoint));

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private sealed class TestWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}
}
