namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Submitting the login form with an empty username field binds <c>Input.UserName</c> to
///     <c>null</c> (the default <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.SimpleTypeModelBinder" />
///     treats an empty posted value as no value for a non-nullable <c>string</c> without
///     <c>AllowEmptyStrings = true</c>), before <c>ModelState.IsValid</c> is checked -- any code that
///     dereferences <c>Input.UserName</c> ahead of that check must tolerate <c>null</c>.
/// </summary>
public sealed partial class LoginValidationTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly SqliteDatabaseFixture database = new();

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();
	}

	public async Task DisposeAsync() => await database.DisposeAsync();

	public void Dispose()
	{
	}

	[Fact]
	public async Task Submitting_an_empty_username_returns_the_login_page_with_validation_errors_instead_of_a_server_error()
	{
		using var factory = new LoginValidationWebApplicationFactory(database.ConnectionString);
		using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

		var form = await GetLoginFormAsync(client);
		var response = await PostLoginAsync(client, form.Token, string.Empty, string.Empty);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("data-valmsg-for=\"Input.UserName\"");
		body.Should().Contain("data-valmsg-for=\"Input.Password\"");
	}

	private static async Task<(HttpResponseMessage Response, string Token)> GetLoginFormAsync(HttpClient client)
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in login page body.");

		return (response, token);
	}

	private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string token, string userName, string password)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

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

	private sealed class LoginValidationWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}
}
