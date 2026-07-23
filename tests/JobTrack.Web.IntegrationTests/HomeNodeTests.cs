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
///     Direct-HTTP tests for the self-service home-node preference: setting/resetting it from
///     <c>/Jobs/Browse</c>, and the no-args <c>/</c> landing redirect honouring it (or falling back to
///     the pre-home-node default of the actor's own active jobs at root when none is set).
/// </summary>
public sealed partial class HomeNodeTests : IAsyncLifetime, IDisposable
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
			UserName = "admin.home-node-tests",
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
	public async Task Landing_with_no_home_node_set_redirects_to_the_unfiltered_root()
	{
		_ = await SeedEmployeeAsync("home-node.default");
		var authCookie = await SignInAsync("home-node.default");

		var response = await GetAsync("/", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var location = response.Headers.Location!.OriginalString;
		// No home node -> the tree root, with no ownership filter applied: the landing never
		// pre-filters Browse to the actor's own nodes (every role sees the whole active tree first).
		location.Should().Contain("/Jobs/Browse");
		location.Should().NotContain("OwnerUserId=");
		location.Should().NotContain("NodeId=");
	}

	[Fact]
	public async Task A_worker_can_set_a_branch_as_their_home_node_and_landing_goes_there()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, "Fit cabinets");
		await SeedEmployeeAsync("home-node.setter");
		var authCookie = await SignInAsync("home-node.setter");

		var (cookie, token) = await GetBrowseFormAsync(authCookie, branchId);
		var setResponse = await PostSetHomeNodeAsync(authCookie, cookie, token, branchId);
		setResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var setReloaded = await FollowRedirectAsync(setResponse, authCookie);
		var setBody = await setReloaded.Content.ReadAsStringAsync();
		setBody.Should().Contain("Home node set");

		var landingResponse = await GetAsync("/", authCookie);

		landingResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var location = landingResponse.Headers.Location!.OriginalString;
		location.Should().Contain($"NodeId={branchId.Value}");
		location.Should().NotContain("OwnerUserId=");
	}

	[Fact]
	public async Task Setting_a_leaf_as_home_node_shows_an_error_and_does_not_change_the_landing_target()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		await SeedEmployeeAsync("home-node.leaf-rejector");
		var authCookie = await SignInAsync("home-node.leaf-rejector");

		var (cookie, token) = await GetBrowseFormAsync(authCookie, leafId);
		var setResponse = await PostSetHomeNodeAsync(authCookie, cookie, token, leafId);

		setResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var setReloaded = await FollowRedirectAsync(setResponse, authCookie);
		var setBody = await setReloaded.Content.ReadAsStringAsync();
		setBody.Should().Contain("leaf cannot be set as a home node");
	}

	[Fact]
	public async Task Resetting_the_home_node_returns_landing_to_the_unfiltered_root()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, "Fit cabinets");
		_ = await SeedEmployeeAsync("home-node.resetter");
		var authCookie = await SignInAsync("home-node.resetter");

		var (setCookie, setToken) = await GetBrowseFormAsync(authCookie, branchId);
		_ = await PostSetHomeNodeAsync(authCookie, setCookie, setToken, branchId);

		var (resetCookie, resetToken) = await GetBrowseFormAsync(authCookie, branchId);
		var resetResponse = await PostResetHomeNodeAsync(authCookie, resetCookie, resetToken);
		resetResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var landingResponse = await GetAsync("/", authCookie);
		var location = landingResponse.Headers.Location!.OriginalString;
		location.Should().Contain("/Jobs/Browse");
		location.Should().NotContain("OwnerUserId=");
		location.Should().NotContain("NodeId=");
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<HttpResponseMessage> PostSetHomeNodeAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=SetHomeNode");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["homeNodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostResetHomeNodeAsync(string authCookie, string antiforgeryCookie, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=ResetHomeNode");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetBrowseFormAsync(string authCookie, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={nodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Browse response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Browse body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
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

	private async Task<AppUserId> SeedEmployeeAsync(string userName)
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
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)EmployeeRole.Worker);
		_ = await insertRole.ExecuteNonQueryAsync();

		return new(appUserId);
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
