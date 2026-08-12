namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Persistence.Sqlite;
using TestSupport;

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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
		client = factory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
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
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.manager", EmployeeRole.JobManager);
		var required = await AddChildAsync(rootId, managerId, "Pour foundation");
		var dependent = await AddChildAsync(rootId, managerId, "Frame walls");
		var authCookie = await client.SignInAsync("prereq.manager");

		var (searchCookie, searchToken) = await GetFormAsync(authCookie, dependent.Id, "Pour");
		var addResponse = await PostAddSelectedAsync(
			authCookie, searchCookie, searchToken, dependent.Id, "Pour", [required.Id], []);
		addResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var addReloaded = await client.FollowRedirectAsync(addResponse, authCookie);
		var addBody = await addReloaded.Content.ReadAsStringAsync();

		addBody.Should().Contain("Dependency added");
		addBody.Should().Contain("Pour foundation");

		var (removeCookie, removeToken) = await WebTestHttp.ExtractFormAsync(addReloaded, searchCookie);
		var removeResponse = await PostRemoveAsync(authCookie, removeCookie, removeToken, dependent.Id, required.Id, dependent.Id);
		removeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var removeReloaded = await client.FollowRedirectAsync(removeResponse, authCookie);
		var removeBody = await removeReloaded.Content.ReadAsStringAsync();

		removeBody.Should().Contain("Prerequisite removed");
	}

	[Fact]
	/// <summary>
	/// The per-edge Remove control is an icon button from the shared sprite, like every other action
	/// repeated once per row or list item. Its accessible name states which edge it removes rather
	/// than repeating a bare "Remove" once per entry, so the names stay distinguishable out of context.
	/// </summary>
	public async Task Each_listed_dependency_offers_an_icon_remove_naming_the_edge_it_removes()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.icon-manager", EmployeeRole.JobManager);
		var required = await AddChildAsync(rootId, managerId, "Pour foundation");
		var dependent = await AddChildAsync(rootId, managerId, "Frame walls");
		var authCookie = await client.SignInAsync("prereq.icon-manager");

		var (searchCookie, searchToken) = await GetFormAsync(authCookie, dependent.Id, "Pour");
		var addResponse = await PostAddSelectedAsync(
			authCookie, searchCookie, searchToken, dependent.Id, "Pour", [required.Id], []);
		var body = await (await client.FollowRedirectAsync(addResponse, authCookie)).Content.ReadAsStringAsync();

		body.Should().Contain("#jt-icon-remove");
		body.Should().Contain("class=\"jt-icon-button\" title=\"Remove dependency\"");
		body.Should().Contain($"Remove dependency on Pour foundation (ID {required.Id.Value})");
		body.Should().NotContain(">Remove</button>");
	}

	[Fact]
	public async Task Readiness_pill_on_browse_reflects_an_added_dependency()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.readiness-manager", EmployeeRole.JobManager);
		var required = await AddChildAsync(rootId, managerId, "Pour foundation");
		var dependent = await AddChildAsync(rootId, managerId, "Frame walls");
		var authCookie = await client.SignInAsync("prereq.readiness-manager");

		var (addCookie, addToken) = await GetFormAsync(authCookie, dependent.Id, "Pour");
		_ = await PostAddSelectedAsync(
			authCookie, addCookie, addToken, dependent.Id, "Pour", [required.Id], []);

		var browseResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={dependent.Id.Value}", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();

		browseBody.Should().Contain("Blocked");
	}

	[Fact]
	public async Task A_worker_who_cannot_manage_either_endpoint_is_denied_when_adding()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.owner-manager", EmployeeRole.JobManager);
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "prereq.denied-worker");
		var required = await AddChildAsync(rootId, managerId, "Owned by manager");
		var dependent = await AddChildAsync(rootId, managerId, "Also owned by manager");
		var authCookie = await client.SignInAsync("prereq.denied-worker");

		var (cookie, token) = await GetFormAsync(authCookie, dependent.Id, "Owned");
		var response = await PostAddSelectedAsync(
			authCookie, cookie, token, dependent.Id, "Owned", [required.Id], []);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = workerId;
	}

	private async Task<JobNodeResult> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description) =>
		await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
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
			new("NodeId", nodeId.Value.ToString(CultureInfo.InvariantCulture)), new("SearchText", searchText), new("__RequestVerificationToken", token),
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
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Prerequisites page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Prerequisites page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}





	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();
}
