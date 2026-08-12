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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
	public async Task Landing_with_no_home_node_set_redirects_to_the_unfiltered_root()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.default");
		var authCookie = await client.SignInAsync("home-node.default");

		var response = await client.GetAuthenticatedAsync("/", authCookie);

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
		await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.setter");
		var authCookie = await client.SignInAsync("home-node.setter");

		var (cookie, token) = await GetBrowseFormAsync(authCookie, branchId);
		var setResponse = await PostSetHomeNodeAsync(authCookie, cookie, token, branchId);
		setResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var setReloaded = await client.FollowRedirectAsync(setResponse, authCookie);
		var setBody = await setReloaded.Content.ReadAsStringAsync();
		setBody.Should().Contain("Home node set");

		var landingResponse = await client.GetAuthenticatedAsync("/", authCookie);

		landingResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var location = landingResponse.Headers.Location!.OriginalString;
		location.Should().Contain($"NodeId={branchId.Value}");
		location.Should().NotContain("OwnerUserId=");
	}

	[Fact]
	public async Task Setting_a_leaf_as_home_node_shows_an_error_and_does_not_change_the_landing_target()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.leaf-rejector");
		var authCookie = await client.SignInAsync("home-node.leaf-rejector");

		var (cookie, token) = await GetBrowseFormAsync(authCookie, leafId);
		var setResponse = await PostSetHomeNodeAsync(authCookie, cookie, token, leafId);

		setResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var setReloaded = await client.FollowRedirectAsync(setResponse, authCookie);
		var setBody = await setReloaded.Content.ReadAsStringAsync();
		setBody.Should().Contain("leaf cannot be set as a home node");
	}

	[Fact]
	/// <summary>
	///     Browsing the node that is already home offers no home-node control at all: "Set as home node"
	///     would be a no-op, and resetting is what browsing somewhere else is for. Root with no home node
	///     set is the same case -- landing already goes there.
	/// </summary>
	public async Task Browsing_the_home_node_offers_no_home_node_button()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, "Fit cabinets");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.no-reset");
		var authCookie = await client.SignInAsync("home-node.no-reset");

		var (setCookie, setToken) = await GetBrowseFormAsync(authCookie, branchId);
		_ = await PostSetHomeNodeAsync(authCookie, setCookie, setToken, branchId);

		var atHome = await client.GetAuthenticatedAsync($"/Jobs/Browse?NodeId={branchId.Value}", authCookie);
		var atHomeBody = await atHome.Content.ReadAsStringAsync();
		atHomeBody.Should().NotContain("Reset home node to root");
		atHomeBody.Should().NotContain("Set as home node");

		// Elsewhere the control is still offered -- and root's own "Set as home node" is how a home node
		// gets moved back to the top, so nothing becomes unreachable.
		var elsewhere = await client.GetAuthenticatedAsync($"/Jobs/Browse?NodeId={rootId.Value}", authCookie);
		var elsewhereBody = await elsewhere.Content.ReadAsStringAsync();
		elsewhereBody.Should().Contain("Set as home node");
	}

	[Fact]
	public async Task Resetting_the_home_node_returns_landing_to_the_unfiltered_root()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, "Fit cabinets");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.resetter");
		var authCookie = await client.SignInAsync("home-node.resetter");

		var (setCookie, setToken) = await GetBrowseFormAsync(authCookie, branchId);
		_ = await PostSetHomeNodeAsync(authCookie, setCookie, setToken, branchId);

		var (resetCookie, resetToken) = await GetBrowseFormAsync(authCookie, branchId);
		var resetResponse = await PostResetHomeNodeAsync(authCookie, resetCookie, resetToken);
		resetResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var landingResponse = await client.GetAuthenticatedAsync("/", authCookie);
		var location = landingResponse.Headers.Location!.OriginalString;
		location.Should().Contain("/Jobs/Browse");
		location.Should().NotContain("OwnerUserId=");
		location.Should().NotContain("NodeId=");
	}

	/// <summary>
	///     The header's "Jobs" link carries no node id, so a bare <c>/Jobs/Browse</c> must root itself at
	///     the actor's own home node — mirroring <c>/Jobs/AwaitingProgress</c>'s own home-node default.
	/// </summary>
	[Fact]
	public async Task Browse_with_no_node_specified_roots_at_the_actors_home_node()
	{
		var homeBranchId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(homeBranchId, "Fit cabinets");
		var otherBranchId = await AddChildAsync(rootId, "Garden landscaping");
		_ = await AddChildAsync(otherBranchId, "Lay turf");
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.browse-default");
		await SetHomeNodeAsync(workerId, homeBranchId);
		var authCookie = await client.SignInAsync("home-node.browse-default");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit cabinets");
		body.Should().NotContain("Lay turf", "the home node's subtree excludes a sibling branch");
	}

	[Fact]
	public async Task Browse_with_no_node_specified_and_no_home_node_roots_at_the_tree_root()
	{
		var homeBranchId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(homeBranchId, "Fit cabinets");
		var otherBranchId = await AddChildAsync(rootId, "Garden landscaping");
		_ = await AddChildAsync(otherBranchId, "Lay turf");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.browse-root");
		var authCookie = await client.SignInAsync("home-node.browse-root");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit cabinets");
		body.Should().Contain("Lay turf");
	}

	/// <summary>
	///     The home-node default applies only when no node was asked for: an explicit node id (the root's
	///     own included, which is what the breadcrumb's root link carries) always wins.
	/// </summary>
	[Fact]
	public async Task Browse_at_an_explicit_root_node_id_overrides_the_home_node_default()
	{
		var homeBranchId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(homeBranchId, "Fit cabinets");
		var otherBranchId = await AddChildAsync(rootId, "Garden landscaping");
		_ = await AddChildAsync(otherBranchId, "Lay turf");
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.browse-override");
		await SetHomeNodeAsync(workerId, homeBranchId);
		var authCookie = await client.SignInAsync("home-node.browse-override");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit cabinets");
		body.Should().Contain("Lay turf");
	}

	/// <summary>
	///     The breadcrumb's root entry must name the root explicitly rather than linking to a bare
	///     <c>/Jobs/Browse</c>, which now means "my home node" — otherwise there is no way up past it.
	/// </summary>
	[Fact]
	public async Task The_breadcrumb_root_link_carries_the_root_node_id()
	{
		var homeBranchId = await AddChildAsync(rootId, "Kitchen renovation");
		var leafId = await AddChildAsync(homeBranchId, "Fit cabinets");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "home-node.breadcrumb");
		var authCookie = await client.SignInAsync("home-node.breadcrumb");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var breadcrumb = BreadcrumbPattern().Match(body);
		breadcrumb.Success.Should().BeTrue();
		breadcrumb.Value.Should().Contain($"/Jobs/Browse?nodeId={rootId.Value}");
	}

	private async Task SetHomeNodeAsync(AppUserId actor, JobNodeId nodeId) =>
		_ = await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() {
				Actor = actor,
				CorrelationId = Guid.NewGuid(),
			},
			NodeId = nodeId,
		});

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
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
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetBrowseFormAsync(string authCookie, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={nodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Browse response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Browse body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}



	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("<nav aria-label=\"breadcrumb\">.*?</nav>", RegexOptions.Singleline)]
	private static partial Regex BreadcrumbPattern();
}
