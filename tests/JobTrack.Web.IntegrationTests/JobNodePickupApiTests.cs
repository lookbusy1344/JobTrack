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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pickup.api.worker");
		var leafId = await AddUnassignedLeafAsync(rootId, "Unassigned pool leaf");
		var authCookie = await client.SignInAsync("pickup.api.worker");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await PostAsync($"/api/jobs/{leafId.Value}/pickup", authCookie, antiforgeryCookie, antiforgeryToken);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.RootElement.GetProperty("ownerUserId").GetInt64().Should().Be(workerId.Value);
	}

	[Fact]
	public async Task Picking_up_an_already_owned_node_receives_a_conflict_problem_response()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pickup.api.owned.worker");
		var otherWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pickup.api.owned.other");
		var leafId = await AddChildAsync(rootId, otherWorkerId, "Already-owned leaf");
		var authCookie = await client.SignInAsync("pickup.api.owned.worker");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await PostAsync($"/api/jobs/{leafId.Value}/pickup", authCookie, antiforgeryCookie, antiforgeryToken);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_can_pick_up_an_unassigned_leaf_via_a_bearer_token()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pickup.api.bearer.worker");
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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pickup.api.contract.viewer");
		var authCookie = await client.SignInAsync("pickup.api.contract.viewer");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{rootId.Value}/children", authCookie);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
		items.Should().ContainSingle(item => item.GetProperty("ownerUserId").ValueKind == JsonValueKind.Null);
	}

	[Fact]
	public async Task The_unassignedOnly_filter_returns_only_pool_children()
	{
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pickup.api.filter.owner");
		_ = await AddChildAsync(rootId, ownerId, "Owned leaf");
		var unassignedLeafId = await AddUnassignedLeafAsync(rootId, "Unassigned pool leaf");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pickup.api.filter.viewer");
		var authCookie = await client.SignInAsync("pickup.api.filter.viewer");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{rootId.Value}/children?unassignedOnly=true", authCookie);
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









	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();



}
