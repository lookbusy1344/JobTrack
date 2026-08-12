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
///     Direct-HTTP tests for the read-only job-context external API surface (plan §4.3 slice 1, ADR
///     0030): <c>/api/jobs/root</c>, <c>/api/jobs/{nodeId}</c>, <c>/api/jobs/{nodeId}/children</c>,
///     <c>/api/jobs/search</c>, and <c>/api/jobs/{nodeId}/readiness</c>. These carry no ownership-based
///     authorization gate (spec §7.3: viewing job data is an unqualified baseline capability for every
///     role), so every test signs in as an ordinary worker unless proving the bearer path specifically.
/// </summary>
public sealed partial class JobContextApiTests : IAsyncLifetime, IDisposable
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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.job-context-tests",
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
	public async Task A_worker_with_a_forced_password_change_cannot_use_the_api_until_the_password_is_changed()
	{
		_ = await SeedEmployeeAsync("jobs.root.worker", true);
		var authCookie = await client.SignInAsync("jobs.root.worker");

		var response = await client.GetAuthenticatedAsync("/api/jobs/root", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("type").GetString()
			.Should().Be(RequiresPasswordChangeEndpointFilter.PasswordChangeRequiredProblemType);
	}

	[Fact]
	public async Task A_worker_can_get_the_root_job_node_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("jobs.root.worker");
		var authCookie = await client.SignInAsync("jobs.root.worker");

		var response = await client.GetAuthenticatedAsync("/api/jobs/root", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("node").GetProperty("id").GetInt64().Should().Be(rootId.Value);
		jsonDocument.RootElement.GetProperty("ancestors").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task Getting_a_leafs_detail_returns_its_ancestors_root_first()
	{
		var workerId = await SeedEmployeeAsync("jobs.detail.worker");
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		var leafId = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var authCookie = await client.SignInAsync("jobs.detail.worker");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{leafId.Value}", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("node").GetProperty("description").GetString().Should().Be("Fit cabinets");
		var ancestors = jsonDocument.RootElement.GetProperty("ancestors");
		ancestors.GetArrayLength().Should().Be(2);
		ancestors[0].GetProperty("id").GetInt64().Should().Be(rootId.Value);
		ancestors[1].GetProperty("description").GetString().Should().Be("Kitchen renovation");
	}

	[Fact]
	public async Task Getting_a_nonexistent_node_returns_problem_details_not_found()
	{
		var workerId = await SeedEmployeeAsync("jobs.detail.missing");
		var authCookie = await client.SignInAsync("jobs.detail.missing");

		var response = await client.GetAuthenticatedAsync("/api/jobs/999999999", authCookie);
		var body = await response.Content.ReadAsStringAsync();
		var jsonDocument = JsonDocument.Parse(body);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("type").GetString().Should().Be("/problems/entity-not-found");
		jsonDocument.RootElement.GetProperty("detail").GetString().Should().Be("The requested resource does not exist.");
		body.Should().NotContain("999999999", "the missing node id is not an existence oracle in the response body (remediation §2.3)");
	}

	[Fact]
	public async Task The_default_archive_filter_hides_an_archived_child_and_All_reveals_it()
	{
		var workerId = await SeedEmployeeAsync("jobs.children.archive");
		var branchId = await AddChildAsync(rootId, workerId, "Decommissioned wing");
		await ArchiveAsync(branchId);
		var authCookie = await client.SignInAsync("jobs.children.archive");

		var activeOnlyResponse = await client.GetAuthenticatedAsync($"/api/jobs/{rootId.Value}/children", authCookie);
		var activeOnlyBody = await activeOnlyResponse.Content.ReadAsStringAsync();
		activeOnlyBody.Should().NotContain("Decommissioned wing");

		var allResponse = await client.GetAuthenticatedAsync($"/api/jobs/{rootId.Value}/children?archiveFilter=All", authCookie);
		var allBody = await allResponse.Content.ReadAsStringAsync();
		allBody.Should().Contain("Decommissioned wing");
	}

	[Fact]
	public async Task Searching_finds_a_matching_descendant_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("jobs.search.worker");
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, workerId, "Fit oak cabinets");
		var authCookie = await client.SignInAsync("jobs.search.worker");

		var response = await client.GetAuthenticatedAsync("/api/jobs/search?searchText=oak", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("items")[0].GetProperty("description").GetString().Should().Be("Fit oak cabinets");
		jsonDocument.RootElement.GetProperty("offset").GetInt32().Should().Be(0);
		jsonDocument.RootElement.GetProperty("hasMore").GetBoolean().Should().BeFalse();
		jsonDocument.RootElement.GetProperty("orderedBy").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Getting_children_bounds_results_by_offset_and_pageSize_and_reports_hasMore()
	{
		var workerId = await SeedEmployeeAsync("jobs.children.paging");
		var branchId = await AddChildAsync(rootId, workerId, "Paging branch");
		_ = await AddChildAsync(branchId, workerId, "Leaf 1");
		_ = await AddChildAsync(branchId, workerId, "Leaf 2");
		_ = await AddChildAsync(branchId, workerId, "Leaf 3");
		var authCookie = await client.SignInAsync("jobs.children.paging");

		var firstPage = await client.GetAuthenticatedAsync($"/api/jobs/{branchId.Value}/children?pageSize=2", authCookie);
		var firstDocument = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
		var secondPage = await client.GetAuthenticatedAsync($"/api/jobs/{branchId.Value}/children?offset=2&pageSize=2", authCookie);
		var secondDocument = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());

		firstDocument.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
		firstDocument.RootElement.GetProperty("pageSize").GetInt32().Should().Be(2);
		firstDocument.RootElement.GetProperty("hasMore").GetBoolean().Should().BeTrue();
		secondDocument.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
		secondDocument.RootElement.GetProperty("hasMore").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task Getting_children_with_an_excessive_pageSize_clamps_rather_than_rejects()
	{
		var workerId = await SeedEmployeeAsync("jobs.children.clamp");
		var branchId = await AddChildAsync(rootId, workerId, "Clamp branch");
		_ = await AddChildAsync(branchId, workerId, "Leaf 1");
		var authCookie = await client.SignInAsync("jobs.children.clamp");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{branchId.Value}/children?pageSize=100000", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("pageSize").GetInt32().Should().Be(200);
	}

	[Fact]
	public async Task Getting_children_with_a_negative_offset_returns_a_validation_problem()
	{
		var workerId = await SeedEmployeeAsync("jobs.children.negative-offset");
		var authCookie = await client.SignInAsync("jobs.children.negative-offset");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{rootId.Value}/children?offset=-1", authCookie);
		var body = await response.Content.ReadAsStringAsync();
		var jsonDocument = JsonDocument.Parse(body);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("type").GetString().Should().Be("/problems/validation");
		jsonDocument.RootElement.GetProperty("detail").GetString().Should().Be("The request contains an invalid value.");
		body.Should().NotContain("Parameter", "the raw .NET exception message/parameter name is not exposed (remediation §2.3)");
		body.Should().NotContain("Actual value");
	}

	[Fact]
	public async Task Readiness_reports_an_unsatisfied_prerequisite_as_a_blocker()
	{
		var workerId = await SeedEmployeeAsync("jobs.readiness.worker");
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		await AddPrerequisiteAsync(requiredLeafId, dependentLeafId);
		var authCookie = await client.SignInAsync("jobs.readiness.worker");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{dependentLeafId.Value}/readiness", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("isReady").GetBoolean().Should().BeFalse();
		jsonDocument.RootElement.GetProperty("blockers")[0].GetProperty("requiredJobId").GetInt64().Should().Be(requiredLeafId.Value);
	}

	[Fact]
	public async Task A_worker_can_browse_the_job_tree_via_a_bearer_token()
	{
		var workerId = await SeedEmployeeAsync("jobs.bearer.worker");
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await GetWithBearerAsync($"/api/jobs/{branchId.Value}", issued.Token);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("node").GetProperty("description").GetString().Should().Be("Kitchen renovation");
	}

	[Fact]
	public async Task An_unauthenticated_request_receives_problem_details_not_a_redirect()
	{
		var response = await client.GetAsync($"/api/jobs/{rootId.Value}");

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
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

	private async Task ArchiveAsync(JobNodeId nodeId)
	{
		var node = await seedClient.Query.GetJobNodeAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = nodeId,
		});

		_ = await seedClient.Jobs.ArchiveAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = nodeId,
			Version = node.Node.Version,
		});
	}

	private async Task AddPrerequisiteAsync(JobNodeId requiredJobId, JobNodeId dependentJobId) =>
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredJobId,
			DependentJobId = dependentJobId,
		});



	private async Task<HttpResponseMessage> GetWithBearerAsync(string path, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Authorization = new("Bearer", token);
		return await client.SendAsync(request);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string userName, bool requiresPasswordChange = false)
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
										 	 $concurrencyStamp, $requiresPasswordChange, 1, 1, 0);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", placeholderUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$requiresPasswordChange", requiresPasswordChange ? 1 : 0);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();

		await using var insertRole = connection.CreateCommand();
		insertRole.CommandText =
			"INSERT INTO identity_user_role (identity_user_id, identity_role_id) SELECT id, $roleId FROM identity_user WHERE app_user_id = $appUserId;";
		_ = insertRole.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)EmployeeRole.Worker);
		_ = await insertRole.ExecuteNonQueryAsync();

		return new(appUserId);
	}





	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();



}
