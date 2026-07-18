namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
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
///     The login page's fixed-window rate limit (Program.cs) is configurable via
///     <c>RateLimiting:LoginPermitLimit</c>/<c>RateLimiting:LoginWindowSeconds</c> so
///     <c>JobTrack.Web.EndToEndTests.BrowserFixture</c> can raise it for its own shared test process
///     without touching the unconfigured production default (20 permits/60s) -- this proves the
///     override actually reaches the rate limiter, not just that the configuration keys parse.
/// </summary>
public sealed partial class LoginRateLimitConfigurationTests : IAsyncLifetime, IDisposable
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
	public async Task A_configured_permit_limit_of_one_rejects_the_second_submitted_attempt_within_the_window()
	{
		using var factory = new ConfiguredRateLimitWebApplicationFactory(database.ConnectionString, 1);
		using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

		var firstForm = await GetLoginFormAsync(client);
		var secondForm = await GetLoginFormAsync(client);
		var first = await PostLoginAsync(client, firstForm.Token, "unknown.user");
		var thirdForm = await GetLoginFormAsync(client);
		var second = await PostLoginAsync(client, thirdForm.Token, "unknown.user");

		firstForm.Response.StatusCode.Should().Be(HttpStatusCode.OK);
		secondForm.Response.StatusCode.Should().Be(HttpStatusCode.OK);
		first.StatusCode.Should().Be(HttpStatusCode.OK, "an invalid submitted login attempt consumes the single permit");
		second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	[Fact]
	public async Task A_configured_permit_limit_of_one_does_not_share_budget_between_different_usernames()
	{
		using var factory = new ConfiguredRateLimitWebApplicationFactory(database.ConnectionString, 1);
		using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

		var firstForm = await GetLoginFormAsync(client);
		var secondForm = await GetLoginFormAsync(client);
		var first = await PostLoginAsync(client, firstForm.Token, "unknown.one");
		var second = await PostLoginAsync(client, secondForm.Token, "unknown.two");

		firstForm.Response.StatusCode.Should().Be(HttpStatusCode.OK);
		secondForm.Response.StatusCode.Should().Be(HttpStatusCode.OK);
		first.StatusCode.Should().Be(HttpStatusCode.OK);
		second.StatusCode.Should().Be(HttpStatusCode.OK, "a different username must have its own credential budget");
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

	private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string token, string userName)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = "wrong-password",
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

	private sealed class ConfiguredRateLimitWebApplicationFactory(string identityConnectionString, int permitLimit)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("RateLimiting:LoginPermitLimit", permitLimit.ToString(CultureInfo.InvariantCulture));
			_ = builder.UseSetting("RateLimiting:LoginWindowSeconds", "60");
		}
	}
}
