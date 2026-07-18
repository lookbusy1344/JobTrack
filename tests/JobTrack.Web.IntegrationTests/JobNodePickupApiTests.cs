namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.Json;
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
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the external HTTP API's pickup route (Stage 8 of the job-node ownership
///     plan) and the nullable-owner/unassigned-pool contract it exposes: <c>POST /api/jobs/{nodeId}/pickup</c>,
///     the <c>ownerUserId</c> result field going from a required <c>long</c> to a nullable one, and the
///     <c>unassignedOnly</c> query parameter distinct from filtering by a specific owner id.
/// </summary>
public sealed partial class JobNodePickupApiTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

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
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.pickup-api-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
		rootId = bootstrap.RootJobNodeId;

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
	public async Task A_worker_can_pick_up_an_unassigned_leaf_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("pickup.api.worker");
		var leafId = await AddUnassignedLeafAsync(rootId, "Unassigned pool leaf");
		var authCookie = await SignInAsync("pickup.api.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		var response = await PostAsync($"/api/jobs/{leafId.Value}/pickup", authCookie, antiforgeryCookie, antiforgeryToken);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.RootElement.GetProperty("ownerUserId").GetInt64().Should().Be(workerId.Value);
	}

	[Fact]
	public async Task Picking_up_an_already_owned_node_receives_a_conflict_problem_response()
	{
		_ = await SeedEmployeeAsync("pickup.api.owned.worker");
		var otherWorkerId = await SeedEmployeeAsync("pickup.api.owned.other");
		var leafId = await AddChildAsync(rootId, otherWorkerId, "Already-owned leaf");
		var authCookie = await SignInAsync("pickup.api.owned.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		var response = await PostAsync($"/api/jobs/{leafId.Value}/pickup", authCookie, antiforgeryCookie, antiforgeryToken);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_can_pick_up_an_unassigned_leaf_via_a_bearer_token()
	{
		var workerId = await SeedEmployeeAsync("pickup.api.bearer.worker");
		var leafId = await AddUnassignedLeafAsync(rootId, "Unassigned pool leaf");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await PostWithBearerAsync($"/api/jobs/{leafId.Value}/pickup", issued.Token);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.RootElement.GetProperty("ownerUserId").GetInt64().Should().Be(workerId.Value);
	}

	[Fact]
	public async Task An_unassigned_child_is_returned_with_a_null_owner_and_no_serialization_error()
	{
		_ = await AddUnassignedLeafAsync(rootId, "Unassigned pool leaf");
		_ = await SeedEmployeeAsync("pickup.api.contract.viewer");
		var authCookie = await SignInAsync("pickup.api.contract.viewer");

		var response = await GetAsync($"/api/jobs/{rootId.Value}/children", authCookie);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
		items.Should().ContainSingle(item => item.GetProperty("ownerUserId").ValueKind == JsonValueKind.Null);
	}

	[Fact]
	public async Task The_unassignedOnly_filter_returns_only_pool_children()
	{
		var ownerId = await SeedEmployeeAsync("pickup.api.filter.owner");
		_ = await AddChildAsync(rootId, ownerId, "Owned leaf");
		var unassignedLeafId = await AddUnassignedLeafAsync(rootId, "Unassigned pool leaf");
		_ = await SeedEmployeeAsync("pickup.api.filter.viewer");
		var authCookie = await SignInAsync("pickup.api.filter.viewer");

		var response = await GetAsync($"/api/jobs/{rootId.Value}/children?unassignedOnly=true", authCookie);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
		items.Should().ContainSingle();
		items[0].GetProperty("id").GetInt64().Should().Be(unassignedLeafId.Value);
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<JobNodeId> AddUnassignedLeafAsync(JobNodeId parentId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = null,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostAsync(string path, string authCookie, string antiforgeryCookie, string antiforgeryToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostWithBearerAsync(string path, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Authorization = new("Bearer", token);
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetAntiforgeryTokenAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery-token");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in token response.");
		var token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()
					?? throw new InvalidOperationException("No antiforgery token in token response.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string userName, EmployeeRole role = EmployeeRole.Worker)
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
