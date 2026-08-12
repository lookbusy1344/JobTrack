namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text;
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
///     Direct-HTTP tests for the external HTTP API's prerequisite and achievement surface (plan §4.3
///     slice 3, ADR 0030): query/add/remove prerequisite edges and get/set a leaf's achievement state.
/// </summary>
public sealed partial class PrerequisiteAndAchievementApiTests : IAsyncLifetime, IDisposable
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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.prereq-tests",
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
	public async Task Listing_prerequisites_bounds_results_by_offset_and_pageSize()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.paging.worker");
		var requiredLeafOneId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var requiredLeafTwoId = await AddChildAsync(rootId, workerId, "Install plumbing");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredLeafOneId,
			DependentJobId = dependentLeafId,
		});
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredLeafTwoId,
			DependentJobId = dependentLeafId,
		});
		var authCookie = await client.SignInAsync("prereq.paging.worker");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{dependentLeafId.Value}/prerequisites?pageSize=1", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
		jsonDocument.RootElement.GetProperty("hasMore").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task A_worker_can_add_and_then_list_a_prerequisite_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.add.worker");
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		var authCookie = await client.SignInAsync("prereq.add.worker");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var addResponse = await PostJsonAsync(
			$"/api/jobs/{dependentLeafId.Value}/prerequisites",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "requiredJobId": {{requiredLeafId.Value}} }""");

		addResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

		var listResponse = await client.GetAuthenticatedAsync($"/api/jobs/{dependentLeafId.Value}/prerequisites", authCookie);
		var listBody = await listResponse.Content.ReadAsStringAsync();

		listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		listBody.Should().Contain($"\"requiredJobId\":{requiredLeafId.Value}");
	}

	[Fact]
	public async Task Retrying_an_identical_add_prerequisite_request_is_rejected_as_a_conflict_not_duplicated()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.add.retry");
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		var authCookie = await client.SignInAsync("prereq.add.retry");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var body = $$"""{ "requiredJobId": {{requiredLeafId.Value}} }""";

		var firstResponse = await PostJsonAsync(
			$"/api/jobs/{dependentLeafId.Value}/prerequisites", authCookie, antiforgeryCookie, antiforgeryToken, body);
		var retryResponse = await PostJsonAsync(
			$"/api/jobs/{dependentLeafId.Value}/prerequisites", authCookie, antiforgeryCookie, antiforgeryToken, body);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task Retrying_a_remove_prerequisite_request_after_success_returns_not_found_not_reapplied()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.remove.retry");
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredLeafId,
			DependentJobId = dependentLeafId,
		});
		var authCookie = await client.SignInAsync("prereq.remove.retry");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var firstResponse = await DeleteAsync(
			$"/api/jobs/{dependentLeafId.Value}/prerequisites/{requiredLeafId.Value}", authCookie, antiforgeryCookie, antiforgeryToken);
		var retryResponse = await DeleteAsync(
			$"/api/jobs/{dependentLeafId.Value}/prerequisites/{requiredLeafId.Value}", authCookie, antiforgeryCookie, antiforgeryToken);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Adding_a_self_referential_prerequisite_is_rejected_as_a_conflict()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.self.worker");
		var leafId = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await client.SignInAsync("prereq.self.worker");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/prerequisites",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "requiredJobId": {{leafId.Value}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_can_remove_a_prerequisite_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.remove.worker");
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredLeafId,
			DependentJobId = dependentLeafId,
		});
		var authCookie = await client.SignInAsync("prereq.remove.worker");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await DeleteAsync(
			$"/api/jobs/{dependentLeafId.Value}/prerequisites/{requiredLeafId.Value}", authCookie, antiforgeryCookie, antiforgeryToken);

		response.StatusCode.Should().Be(HttpStatusCode.NoContent);

		var listResponse = await client.GetAuthenticatedAsync($"/api/jobs/{dependentLeafId.Value}/prerequisites", authCookie);
		var listBody = await listResponse.Content.ReadAsStringAsync();
		listBody.Should().NotContain($"\"requiredJobId\":{requiredLeafId.Value}");
	}

	[Fact]
	public async Task A_worker_can_get_a_leafs_achievement_state_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achieve.get.worker");
		var leafId = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await client.SignInAsync("achieve.get.worker");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{leafId.Value}/achievement", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("achievement").GetString().Should().Be(nameof(Achievement.Waiting));
	}

	[Fact]
	public async Task A_worker_can_transition_a_leafs_achievement_with_a_reason_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achieve.set.worker");
		var leafId = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await client.SignInAsync("achieve.set.worker");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var current = await seedClient.Query.GetLeafWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
		});

		var response = await PutJsonAsync(
			$"/api/jobs/{leafId.Value}/achievement",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "newAchievement": "InProgress",
			    "reason": "Started fitting the cabinets",
			    "version": {{current.Version}}
			  }
			  """);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("achievement").GetString().Should().Be(nameof(Achievement.InProgress));
	}

	[Fact]
	public async Task Retrying_an_achievement_transition_with_the_now_stale_version_is_rejected_as_a_conflict_not_reapplied()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achieve.set.retry");
		var leafId = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await client.SignInAsync("achieve.set.retry");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var current = await seedClient.Query.GetLeafWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
		});
		var requestBody = $$"""
							{
							  "newAchievement": "InProgress",
							  "reason": "Started fitting the cabinets",
							  "version": {{current.Version}}
							}
							""";

		var firstResponse = await PutJsonAsync($"/api/jobs/{leafId.Value}/achievement", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);
		var retryResponse = await PutJsonAsync($"/api/jobs/{leafId.Value}/achievement", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task Reopening_a_terminal_achievement_without_privilege_is_denied()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achieve.reopen.worker");
		var leafId = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var cancelled = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			NewAchievement = Achievement.Cancelled,
			Reason = "No longer needed",
			Version = 1,
		});
		var authCookie = await client.SignInAsync("achieve.reopen.worker");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await PutJsonAsync(
			$"/api/jobs/{leafId.Value}/achievement",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "newAchievement": "Waiting",
			    "reason": "Reopening without authority",
			    "version": {{cancelled.Version}}
			  }
			  """);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_can_add_a_prerequisite_via_a_bearer_token_without_an_antiforgery_token()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.bearer.worker");
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await PostJsonWithBearerAsync(
			$"/api/jobs/{dependentLeafId.Value}/prerequisites", issued.Token, $$"""{ "requiredJobId": {{requiredLeafId.Value}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.NoContent);
	}

	[Fact]
	public async Task A_worker_cannot_set_achievement_on_a_leaf_in_a_sibling_subtree_they_do_not_own()
	{
		var ownerA = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achieve.sibling.owner-a");
		var ownerB = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achieve.sibling.owner-b");
		_ = await AddChildAsync(rootId, ownerA, "Branch A");
		var branchB = await AddChildAsync(rootId, ownerB, "Branch B");
		var leafUnderB = await AddWorkedLeafAsync(branchB, administratorId, "Leaf under B");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = ownerA, CorrelationId = Guid.NewGuid() },
			TargetUserId = ownerA,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await PutJsonWithBearerAsync(
			$"/api/jobs/{leafUnderB.Value}/achievement",
			issued.Token,
			"""{ "newAchievement": "InProgress", "reason": "Sibling-subtree access attempt", "version": 1 }""");

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

	private async Task<JobNodeId> AddWorkedLeafAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = result.Id,
		});

		return result.Id;
	}



	private async Task<HttpResponseMessage> PostJsonAsync(
		string path, string authCookie, string antiforgeryCookie, string antiforgeryToken, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PutJsonAsync(
		string path, string authCookie, string antiforgeryCookie, string antiforgeryToken, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Put, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> DeleteAsync(string path, string authCookie, string antiforgeryCookie, string antiforgeryToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Delete, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostJsonWithBearerAsync(string path, string token, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Authorization = new("Bearer", token);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PutJsonWithBearerAsync(string path, string token, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Put, path);
		request.Headers.Authorization = new("Bearer", token);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}









	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();



}
