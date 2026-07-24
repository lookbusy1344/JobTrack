namespace JobTrack.Web.IntegrationTests;

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
///     Direct-HTTP tests for job-tree navigation on <c>/Jobs/Browse</c>: the breadcrumb path to root,
///     the prerequisite/dependent link lists, and the same links carried onto
///     <c>/Jobs/Prerequisites</c>.
/// </summary>
public sealed partial class JobBrowseNavigationTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId adminId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId rootId;
	private IJobTrackClient seedClient = null!;
	private AppUserId workerId;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.browse-nav",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrap.RootJobNodeId;
		adminId = bootstrap.AdministratorId;
		workerId = await SeedWorkerEmployeeAsync("browse-nav.worker");

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
	public async Task Browsing_the_root_shows_a_breadcrumb_with_no_ancestor_links()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("aria-label=\"breadcrumb\"");
		body.Should().NotContain($"href=\"/Jobs/Browse?nodeId={rootId.Value}\"");
	}

	[Fact]
	public async Task The_owner_filter_renders_as_a_named_dropdown_not_a_numeric_input()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		// The owner filter now lives only on the Search flow (Ownership/ArchiveFilter scope a
		// whole-tree search, not the currently browsed subtree), reached via the toolbar's "Search"
		// link -- the blank search-entry view, before any SearchText is submitted.
		var response = await GetAsync("/Jobs/Browse?search=true", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		// The owner filter is a <select> of employee names defaulting to "All owners" (no filter),
		// never a bare numeric id <input> (every filter shows names, not raw AppUserId values).
		body.Should().MatchRegex("<select[^>]*id=\"OwnerUserId\"");
		body.Should().NotMatchRegex("<input[^>]*id=\"OwnerUserId\"");
		body.Should().Contain("All owners");
		body.Should().Contain("browse-nav.worker (browse-nav.worker)");
	}

	[Fact]
	public async Task Search_remembers_the_owner_filter_across_a_return_visit()
	{
		var workerId = await SeedEmployeeAsync("browse-nav.filtermem", EmployeeRole.Worker);
		_ = await AddChildAsync(rootId, "Admin owned oak cabinet");
		var authCookie = await SignInAsync("browse-nav.worker");

		// Explicitly filter a search to the worker (who owns nothing here); capture the session that
		// now remembers the choice, and sanity-check the filter actually hides the admin's match.
		using var chooseRequest = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?searchText=oak&ownerUserId={workerId.Value}");
		chooseRequest.Headers.Add("Cookie", authCookie);
		var chooseResponse = await client.SendAsync(chooseRequest);
		(await ReadNormalizedBodyAsync(chooseResponse)).Should().NotContain("Admin owned oak cabinet");
		var sessionCookie = ExtractCookiePair(
			FindSetCookie(chooseResponse, "JobTrack.Session") ?? throw new InvalidOperationException("No session cookie was set."));

		// Search again with no owner param: the remembered worker filter still hides the admin's match.
		using var returnRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/Browse?searchText=oak");
		returnRequest.Headers.Add("Cookie", $"{authCookie}; {sessionCookie}");
		var returnResponse = await client.SendAsync(returnRequest);
		var returnBody = await ReadNormalizedBodyAsync(returnResponse);

		returnBody.Should().NotContain("Admin owned oak cabinet");
	}

	[Fact]
	public async Task Search_defaults_to_all_owners_when_nothing_is_remembered()
	{
		_ = await AddChildAsync(rootId, "Admin owned oak cabinet");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync("/Jobs/Browse?searchText=oak", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("Admin owned oak cabinet", "with nothing remembered Search defaults to all owners");
	}

	[Fact]
	public async Task Browsing_a_direct_child_of_root_shows_a_breadcrumb_link_to_root()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("aria-label=\"breadcrumb\"");
		body.Should().Contain("href=\"/Jobs/Browse\"");
		body.Should().NotContain($"href=\"/Jobs/Browse?nodeId={branchId.Value}\"");
	}

	[Fact]
	public async Task Browsing_a_grandchild_shows_breadcrumb_links_to_root_and_its_immediate_parent()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		var leafId = await AddChildAsync(branchId, "Fit cabinets");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("href=\"/Jobs/Browse\"");
		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={branchId.Value}\"");
		body.Should().Contain("Kitchen renovation");
	}

	[Fact]
	public async Task Browsing_the_root_hides_prerequisite_and_work_sections()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().NotContain("Requires (must finish first)");
		body.Should().NotContain("Depends on this job");
		body.Should().NotContain(">Dependencies<");
		body.Should().NotContain(">Decompose<");
		body.Should().NotContain(">Work<");
	}

	[Fact]
	public async Task Browsing_a_leaf_without_prerequisites_hides_the_requires_section_but_still_shows_work_controls()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		// Neither Requires nor Depends-on has an edge on this leaf, so the shared card is hidden
		// entirely rather than rendered with two "None." lists.
		body.Should().NotContain("Requires (must finish first)");
		body.Should().NotContain("Depends on this job");
		body.Should().Contain(">Dependencies<");
		body.Should().Contain(">Decompose<");
		body.Should().Contain("#jt-icon-start");
	}

	[Fact]
	public async Task Prerequisite_and_dependent_links_render_with_descriptions_including_an_archived_node()
	{
		var requiredLeafId = await AddChildAsync(rootId, "Old survey");
		var dependentLeafId = await AddChildAsync(rootId, "Frame walls");
		await ArchiveAsync(requiredLeafId);
		await AddPrerequisiteAsync(requiredLeafId, dependentLeafId);
		var authCookie = await SignInAsync("browse-nav.worker");

		var dependentResponse = await GetAsync($"/Jobs/Browse?nodeId={dependentLeafId.Value}", authCookie);
		var dependentBody = await ReadNormalizedBodyAsync(dependentResponse);
		dependentBody.Should().Contain("Requires (must finish first)");
		dependentBody.Should().Contain($"href=\"/Jobs/Browse?nodeId={requiredLeafId.Value}\"");
		dependentBody.Should().Contain("Old survey");

		var requiredResponse = await GetAsync($"/Jobs/Browse?nodeId={requiredLeafId.Value}", authCookie);
		var requiredBody = await ReadNormalizedBodyAsync(requiredResponse);
		requiredBody.Should().Contain("Depends on this job");
		requiredBody.Should().Contain($"href=\"/Jobs/Browse?nodeId={dependentLeafId.Value}\"");
		requiredBody.Should().Contain("Frame walls");
	}

	[Fact]
	public async Task Search_browse_button_returns_to_the_last_browsed_node()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		// Browse the leaf first; capture the session that now remembers it as the last-browsed node.
		var browseResponse = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var sessionCookie = ExtractCookiePair(
			FindSetCookie(browseResponse, "JobTrack.Session") ?? throw new InvalidOperationException("No session cookie was set."));

		using var searchRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/Browse?search=true");
		searchRequest.Headers.Add("Cookie", $"{authCookie}; {sessionCookie}");
		var searchResponse = await client.SendAsync(searchRequest);
		var searchBody = await ReadNormalizedBodyAsync(searchResponse);

		searchBody.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\">Browse</a>");
	}

	[Fact]
	public async Task Search_browse_button_falls_back_to_the_home_node_when_nothing_was_browsed_this_session()
	{
		// A home node must be a branch (or the root), never a leaf -- give it a child so its derived
		// kind (ADR 0035) is Branch.
		var homeNodeId = await AddChildAsync(rootId, "Kitchen renovation");
		_ = await AddChildAsync(homeNodeId, "Fit cabinets");
		await SetWorkerHomeNodeAsync(homeNodeId);
		var authCookie = await SignInAsync("browse-nav.worker");

		// A fresh session with nothing browsed yet -- the home node set above is the only fallback.
		var response = await GetAsync("/Jobs/Browse?search=true", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={homeNodeId.Value}\">Browse</a>");
	}

	[Fact]
	public async Task Search_browse_button_falls_back_to_the_root_when_nothing_is_remembered_or_set()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		// A fresh session, no home node configured: the last-resort fallback is the root, i.e. a
		// plain Browse link carrying no node id at all.
		var response = await GetAsync("/Jobs/Browse?search=true", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("href=\"/Jobs/Browse\">Browse</a>");
	}

	[Fact]
	public async Task Prerequisites_page_links_to_the_related_job_by_description_instead_of_a_bare_id()
	{
		var requiredLeafId = await AddChildAsync(rootId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, "Frame walls");
		await AddPrerequisiteAsync(requiredLeafId, dependentLeafId);
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Prerequisites?nodeId={dependentLeafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={requiredLeafId.Value}\"");
		body.Should().Contain("Pour foundation");
		body.Should().NotContain($"Job {requiredLeafId.Value}</span>");
	}

	// Stage 5 navigation audit: every node-presenting specialist page links that node's name back to
	// Browse (plan §2.1 rule 2), rather than showing it as unlinked plain text.

	[Fact]
	public async Task Move_page_links_the_moved_nodes_own_name_to_browse()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		var leafId = await AddChildAsync(branchId, "Fit cabinets");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Move?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={branchId.Value}\"");
	}

	[Fact]
	public async Task Decompose_page_links_the_decomposed_leafs_own_name_to_browse()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Decompose?leafNodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
	}

	[Fact]
	public async Task Delete_page_links_the_targeted_nodes_own_name_to_browse()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Delete?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
	}

	[Fact]
	public async Task CostReport_page_links_the_reported_nodes_own_name_to_browse()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		await SeedEmployeeAsync("browse-nav.cost-viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("browse-nav.cost-viewer");

		var response = await GetAsync($"/Jobs/CostReport?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
	}

	[Fact]
	public async Task Work_page_links_the_leafs_own_name_to_browse_and_titles_the_page_with_the_leafs_description()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
		body.Should().Contain("Pour foundation</h1>");
		body.Should().NotContain("jt-eyebrow", "the eyebrow kicker was removed project-wide -- a page shows one title, not two");
		body.Should().NotContain("Leaf work");
	}

	[Fact]
	public async Task Create_page_links_the_named_parent_to_browse()
	{
		var parentId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Create?parentId={parentId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={parentId.Value}\">Kitchen renovation (ID {parentId.Value})</a>");
	}

	[Fact]
	public async Task Edit_page_names_the_target_node_and_links_it_to_browse()
	{
		var nodeId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Edit?nodeId={nodeId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={nodeId.Value}\">Kitchen renovation (ID {nodeId.Value})</a>");
	}

	[Fact]
	public async Task Achievement_page_redirects_to_the_unified_work_page_for_the_same_leaf()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			WorkedByUserId = adminId,
		});
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Achievement?jobNodeId={leafId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be($"/Jobs/Work?leafNodeId={leafId.Value}#status");
	}

	[Fact]
	public async Task Browse_leaf_toolbar_and_row_both_render_a_sessions_link_with_the_shared_icon()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var leafResponse = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var leafBody = await ReadNormalizedBodyAsync(leafResponse);
		leafBody.Should().Contain($"href=\"/Jobs/Work?leafNodeId={leafId.Value}\"");
		leafBody.Should().Contain("#jt-icon-sessions");

		var rootResponse = await GetAsync("/Jobs/Browse", authCookie);
		var rootBody = await ReadNormalizedBodyAsync(rootResponse);
		rootBody.Should().Contain($"href=\"/Jobs/Work?leafNodeId={leafId.Value}\"");
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = adminId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task ArchiveAsync(JobNodeId nodeId)
	{
		var node = await seedClient.Query.GetJobNodeAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			NodeId = nodeId,
		});

		_ = await seedClient.Jobs.ArchiveAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			NodeId = nodeId,
			Version = node.Node.Version,
		});
	}

	private async Task AddPrerequisiteAsync(JobNodeId requiredJobId, JobNodeId dependentJobId) =>
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredJobId,
			DependentJobId = dependentJobId,
		});

	private async Task SetWorkerHomeNodeAsync(JobNodeId nodeId) =>
		await seedClient.Employees.SetHomeNodeAsync(new() { Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() }, NodeId = nodeId });

	private static async Task<string> ReadNormalizedBodyAsync(HttpResponseMessage response) =>
		WhitespaceRunPattern().Replace(await response.Content.ReadAsStringAsync(), " ");

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
	}

	private Task<string> SignInAsync(string userName) => SignInAsync(userName, KnownPassword);

	private async Task<string> SignInAsync(string userName, string password)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync();

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

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRunPattern();

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

	private Task<AppUserId> SeedWorkerEmployeeAsync(string userName) => SeedEmployeeAsync(userName, EmployeeRole.Worker);

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
