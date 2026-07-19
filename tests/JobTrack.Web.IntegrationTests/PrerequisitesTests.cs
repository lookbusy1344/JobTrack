namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for prerequisite editing (plan §8.5 slice 5, spec §6): adding and removing
///     prerequisite edges in either direction from the current node.
/// </summary>
public sealed partial class PrerequisitesTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId administratorId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId rootId;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.prereq-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;
		administratorId = bootstrapResult.AdministratorId;

		factory = new(database.ConnectionString);
		client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
	}

	public async Task DisposeAsync()
	{
		Dispose();
		await database.DisposeAsync();
	}

	public void Dispose()
	{
		client.Dispose();
		factory.Dispose();
	}

	[Fact]
	public async Task A_job_manager_can_search_add_and_then_remove_a_dependency()
	{
		var managerId = await SeedEmployeeAsync("prereq.manager", EmployeeRole.JobManager);
		var required = await AddChildAsync(rootId, managerId, "Pour foundation");
		var dependent = await AddChildAsync(rootId, managerId, "Frame walls");
		var authCookie = await SignInAsync("prereq.manager");

		var (searchCookie, searchToken) = await GetFormAsync(authCookie, dependent.Id, "Pour");
		var addResponse = await PostAddSelectedAsync(
			authCookie, searchCookie, searchToken, dependent.Id, "Pour", [required.Id], []);
		addResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var addReloaded = await FollowRedirectAsync(addResponse, authCookie);
		var addBody = await addReloaded.Content.ReadAsStringAsync();

		addBody.Should().Contain("Dependency added");
		addBody.Should().Contain("Pour foundation");

		var (removeCookie, removeToken) = await ExtractFormAsync(addReloaded, searchCookie);
		var removeResponse = await PostRemoveAsync(authCookie, removeCookie, removeToken, dependent.Id, required.Id, dependent.Id);
		removeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var removeReloaded = await FollowRedirectAsync(removeResponse, authCookie);
		var removeBody = await removeReloaded.Content.ReadAsStringAsync();

		removeBody.Should().Contain("Prerequisite removed");
	}

	[Fact]
	public async Task Readiness_pill_on_browse_reflects_an_added_dependency()
	{
		var managerId = await SeedEmployeeAsync("prereq.readiness-manager", EmployeeRole.JobManager);
		var required = await AddChildAsync(rootId, managerId, "Pour foundation");
		var dependent = await AddChildAsync(rootId, managerId, "Frame walls");
		var authCookie = await SignInAsync("prereq.readiness-manager");

		var (addCookie, addToken) = await GetFormAsync(authCookie, dependent.Id, "Pour");
		_ = await PostAddSelectedAsync(
			authCookie, addCookie, addToken, dependent.Id, "Pour", [required.Id], []);

		var browseResponse = await GetAsync($"/Jobs/Browse?nodeId={dependent.Id.Value}", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();

		browseBody.Should().Contain("Blocked");
	}

	[Fact]
	public async Task A_worker_who_cannot_manage_either_endpoint_is_denied_when_adding()
	{
		var managerId = await SeedEmployeeAsync("prereq.owner-manager", EmployeeRole.JobManager);
		var workerId = await SeedEmployeeAsync("prereq.denied-worker", EmployeeRole.Worker);
		var required = await AddChildAsync(rootId, managerId, "Owned by manager");
		var dependent = await AddChildAsync(rootId, managerId, "Also owned by manager");
		var authCookie = await SignInAsync("prereq.denied-worker");

		var (cookie, token) = await GetFormAsync(authCookie, dependent.Id, "Owned");
		var response = await PostAddSelectedAsync(
			authCookie, cookie, token, dependent.Id, "Owned", [required.Id], []);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = workerId;
	}

	private async Task<JobNodeResult> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description) =>
		await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

	private async Task<HttpResponseMessage> PostAddSelectedAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId nodeId, string searchText,
		IReadOnlyCollection<JobNodeId> requiresIds, IReadOnlyCollection<JobNodeId> requiredByIds)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Prerequisites?handler=AddSelected");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");

		var form = new List<KeyValuePair<string, string>> {
			new("NodeId", nodeId.Value.ToString(CultureInfo.InvariantCulture)),
			new("SearchText", searchText),
			new("__RequestVerificationToken", token),
		};
		form.AddRange(requiresIds.Select(id =>
			new KeyValuePair<string, string>($"Input.Selections[{id.Value.ToString(CultureInfo.InvariantCulture)}]", "Requires")));
		form.AddRange(requiredByIds.Select(id =>
			new KeyValuePair<string, string>($"Input.Selections[{id.Value.ToString(CultureInfo.InvariantCulture)}]", "RequiredBy")));
		request.Content = new FormUrlEncodedContent(form);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostRemoveAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId nodeId, JobNodeId requiredJobId, JobNodeId dependentJobId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Prerequisites?handler=Remove");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["NodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["requiredJobId"] = requiredJobId.Value.ToString(CultureInfo.InvariantCulture),
			["dependentJobId"] = dependentJobId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, JobNodeId nodeId, string? searchText = null)
	{
		var query = searchText is null
			? $"?nodeId={nodeId.Value}"
			: $"?nodeId={nodeId.Value}&SearchText={Uri.EscapeDataString(searchText)}";
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Prerequisites{query}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Prerequisites page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Prerequisites page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static async Task<(string CookieHeader, string Token)> ExtractFormAsync(HttpResponseMessage response, string previousAntiforgeryCookie)
	{
		var body = await response.Content.ReadAsStringAsync();
		var cookie = FindSetCookie(response, "Antiforgery") is { } newCookie ? ExtractCookiePair(newCookie) : previousAntiforgeryCookie;
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in response body.");

		return (cookie, token);
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
	}

	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	private async Task<HttpResponseMessage> FollowRedirectAsync(HttpResponseMessage response, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
		var cookieHeader = string.Join("; ", new[] { authCookie }.Concat(ExtractSetCookiePairs(response)));
		request.Headers.Add("Cookie", cookieHeader);

		return await client.SendAsync(request);
	}

	private static IEnumerable<string> ExtractSetCookiePairs(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(ExtractCookiePair) : [];

	private async Task<string> SignInAsync(string userName)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return ExtractCookiePair(authCookie);
	}

	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
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

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private async Task<AppUserId> SeedEmployeeAsync(string userName, EmployeeRole role)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		var appUserId = (long)(await insertAppUser.ExecuteScalarAsync())!;

		var placeholderUser = new JobTrackIdentityUser {
			AppUserId = new(appUserId),
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = string.Empty,
			SecurityStamp = Guid.NewGuid().ToString(),
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(placeholderUser, KnownPassword);

		await using var insertIdentityUser = connection.CreateCommand();
		insertIdentityUser.CommandText = """
										 INSERT INTO identity_user
										 	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										 	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										 VALUES
										 	($appUserId, $userName, $normalizedUserName, $passwordHash, $securityStamp,
										 	 $concurrencyStamp, 0, 1, 1, 0);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", placeholderUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();

		await using var insertRole = connection.CreateCommand();
		insertRole.CommandText =
			"INSERT INTO identity_user_role (identity_user_id, identity_role_id) SELECT id, $roleId FROM identity_user WHERE app_user_id = $appUserId;";
		_ = insertRole.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)role);
		_ = await insertRole.ExecuteNonQueryAsync();

		return new(appUserId);
	}

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
