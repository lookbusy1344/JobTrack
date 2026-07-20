namespace JobTrack.Web.EndToEndTests;

using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     End-to-end smoke coverage over the real web host for both supported providers: sign in through
///     the HTML flow, browse the job tree, and hit the initial JSON/OpenAPI endpoints. This gives the
///     otherwise-empty end-to-end project a concrete operational path on both SQLite and PostgreSQL.
/// </summary>
public sealed partial class ProviderSmokeTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	[Fact]
	public async Task The_sqlite_host_supports_a_basic_operational_flow()
	{
		await using var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();
		await DeploySchemaAsync(database, SchemaProvider.Sqlite);

		var admin = await BootstrapAdministratorAsync(
			() => JobTrackSqlite.Create(database.ConnectionString),
			"admin.sqlite.e2e",
			"Sqlite-Horse-Battery-42!");

		using var factory = new TestWebApplicationFactory(SchemaProvider.Sqlite, database.ConnectionString);
		using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

		var authCookie = await SignInAsync(client, admin.UserName, admin.Password);

		await AssertOperationalFlowAsync(client, authCookie, admin.AdministratorId.Value);
	}

	[Fact]
	public async Task The_postgresql_host_supports_a_basic_operational_flow()
	{
		await using var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();
		await DeploySchemaAsync(database, SchemaProvider.PostgreSql);

		var admin = await BootstrapAdministratorAsync(
			() => JobTrackPostgreSql.Create(new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build()),
			"admin.postgresql.e2e",
			"PostgreSql-Horse-Battery-42!");

		using var factory = new TestWebApplicationFactory(SchemaProvider.PostgreSql, database.ConnectionString);
		using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

		var authCookie = await SignInAsync(client, admin.UserName, admin.Password);

		await AssertOperationalFlowAsync(client, authCookie, admin.AdministratorId.Value);
	}

	private static async Task AssertOperationalFlowAsync(HttpClient client, string authCookie, long administratorId)
	{
		var browseResponse = await GetAsync(client, "/Jobs/Browse", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();
		browseResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		browseBody.Should().Contain("Root");

		var scheduleResponse = await GetAsync(client, $"/api/employees/{administratorId}/schedule", authCookie);
		scheduleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		scheduleResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

		var ratesResponse = await GetAsync(client, $"/api/employees/{administratorId}/rates", authCookie);
		ratesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		ratesResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

		var openApiResponse = await GetAsync(client, "/openapi/v1.json", authCookie);
		var openApiBody = await openApiResponse.Content.ReadAsStringAsync();
		openApiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		openApiBody.Should().Contain("/api/employees/{userId}/rates");
	}

	private static async Task<(AppUserId AdministratorId, string UserName, string Password)> BootstrapAdministratorAsync(
		Func<IJobTrackClient> createClient,
		string userName,
		string password)
	{
		var client = createClient();
		var bootstrap = await client.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = userName,
			Password = password,
			CorrelationId = Guid.NewGuid(),
		});

		return (bootstrap.AdministratorId, userName, password);
	}

	private static async Task DeploySchemaAsync(IDisposableTestDatabase database, SchemaProvider provider)
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(provider));

		switch (provider) {
			case SchemaProvider.Sqlite:
				await using (var connection = new SqliteConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					await using (var pragma = connection.CreateCommand()) {
						pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
						_ = await pragma.ExecuteNonQueryAsync();
					}

					var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(),
						ApplicationVersion, AppliedBy);
					await deployer.DeployAsync(scripts, CancellationToken.None);
				}

				break;
			case SchemaProvider.PostgreSql:
				await using (var connection = new NpgsqlConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					var deployer = new SchemaDeployer(connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(),
						ApplicationVersion, AppliedBy);
					await deployer.DeployAsync(scripts, CancellationToken.None);
				}

				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.");
		}
	}

	private static async Task<string> SignInAsync(HttpClient client, string userName, string password)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync(client);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");
		var authCookiePair = ExtractCookiePair(authCookie);

		if (response.Headers.Location?.OriginalString.Contains("/Account/ChangePassword", StringComparison.Ordinal) == true) {
			return await CompleteForcedPasswordChangeAsync(client, authCookiePair, password, $"{password}.changed");
		}

		return authCookiePair;
	}

	private static async Task<(string CookieHeader, string Token)> GetLoginFormAsync(HttpClient client)
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in login page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static async Task<string> CompleteForcedPasswordChangeAsync(
		HttpClient client,
		string authCookie,
		string currentPassword,
		string newPassword)
	{
		using var getRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/ChangePassword");
		getRequest.Headers.Add("Cookie", authCookie);
		var getResponse = await client.SendAsync(getRequest);
		var body = await getResponse.Content.ReadAsStringAsync();
		var antiforgeryCookie = ExtractCookiePair(
			FindSetCookie(getResponse, "Antiforgery") ??
			throw new InvalidOperationException("No antiforgery cookie in change-password page response."));
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in change-password page body.");

		using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		postRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		postRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = currentPassword,
			["Input.NewPassword"] = newPassword,
			["Input.ConfirmNewPassword"] = newPassword,
			["__RequestVerificationToken"] = token,
		});
		var postResponse = await client.SendAsync(postRequest);
		postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var refreshedCookie = FindSetCookie(postResponse, "Identity.Application");
		return refreshedCookie is not null
			? ExtractCookiePair(refreshedCookie)
			: authCookie;
	}

	private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private sealed class TestWebApplicationFactory(SchemaProvider provider, string connectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", provider.ToString());
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", connectionString);
		}
	}
}
