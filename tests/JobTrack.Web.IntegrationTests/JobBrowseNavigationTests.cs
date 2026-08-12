namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
	public async Task Browsing_the_root_shows_a_breadcrumb_with_no_ancestor_links()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
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
		var response = await client.GetAuthenticatedAsync("/Jobs/Browse?search=true", authCookie);
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
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "browse-nav.filtermem");
		_ = await AddChildAsync(rootId, "Admin owned oak cabinet");
		var authCookie = await SignInAsync("browse-nav.worker");

		// Explicitly filter a search to the worker (who owns nothing here); capture the session that
		// now remembers the choice, and sanity-check the filter actually hides the admin's match.
		using var chooseRequest = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?searchText=oak&ownerUserId={workerId.Value}");
		chooseRequest.Headers.Add("Cookie", authCookie);
		var chooseResponse = await client.SendAsync(chooseRequest);
		(await ReadNormalizedBodyAsync(chooseResponse)).Should().NotContain("Admin owned oak cabinet");
		var sessionCookie = WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(chooseResponse, "JobTrack.Filters") ?? throw new InvalidOperationException("No session cookie was set."));

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

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse?searchText=oak", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("Admin owned oak cabinet", "with nothing remembered Search defaults to all owners");
	}

	[Fact]
	public async Task Browsing_a_direct_child_of_root_shows_a_breadcrumb_link_to_root()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
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

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("href=\"/Jobs/Browse\"");
		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={branchId.Value}\"");
		body.Should().Contain("Kitchen renovation");
	}

	[Fact]
	public async Task Browsing_a_node_with_a_deadline_shows_it_as_its_own_field_below_priority()
	{
		// Far enough in the future to stay non-overdue (no jt-overdue class) for the life of this test.
		var deadline = Instant.FromUtc(2030, 7, 26, 12, 0);
		var branch = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Kitchen renovation",
			OwnerUserId = adminId,
			Priority = Priority.High,
			NeededFinish = deadline,
		});
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branch.Id.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		// Its own dt/dd pair, and the priority label no longer carries the deadline as a parenthetical.
		// The remaining-time suffix moves with the calendar, so only the stamp is asserted here --
		// InstantDisplayDeadlineTests owns the suffix.
		body.Should().Contain("class=\"jt-priority jt-priority--high\">High</span>");
		body.Should().NotContain("(deadline");
		body.Should().Contain("<dt class=\"col-12 col-sm-4\">Deadline</dt>");
		body.Should().Contain("<span>26 Jul 2030 12:00 (");
	}

	[Fact]
	/// <summary>
	///     The record card's fields run in one fixed order -- Kind, Owner, Priority, Cost, Deadline,
	///     Achievement, Readiness, Active -- which the two-up md+ grid pairs into rows so that Deadline
	///     falls directly under Priority. Asserted on the labels' document order, the only thing that
	///     decides which grid cell each field lands in.
	/// </summary>
	public async Task The_record_card_fields_run_in_their_fixed_order()
	{
		var branch = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Field order branch",
			OwnerUserId = adminId,
			Priority = Priority.High,
			NeededFinish = Instant.FromUtc(2030, 7, 26, 12, 0),
		});
		// Leaf work attached so the node carries an Achievement to place; without it that field is absent
		// and the order it sits in goes unchecked.
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = branch.Id,
		});
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branch.Id.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		// Cost and Active are conditional (cost visibility, an open session), so the fields this node
		// does show are checked in order rather than the whole list being demanded.
		var labels = FieldLabelPattern().Matches(body).Select(match => match.Groups["label"].Value).ToArray();
		labels.Should().ContainInOrder("Kind", "Owner", "Priority", "Deadline", "Achievement", "Readiness");
	}

	[Fact]
	/// <summary>
	///     A deadline that has already passed renders red (jt-overdue, InstantDisplay.IsPast) so it
	///     stands out from an ordinary future one -- checked both ways round on the same field.
	/// </summary>
	public async Task A_past_deadline_renders_red_and_a_future_one_does_not()
	{
		var pastDeadline = Instant.FromUtc(2020, 1, 1, 12, 0);
		var pastBranch = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Overdue deadline branch",
			OwnerUserId = adminId,
			Priority = Priority.High,
			NeededFinish = pastDeadline,
		});
		var futureDeadline = Instant.FromUtc(2030, 1, 1, 12, 0);
		var futureBranch = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Future deadline branch",
			OwnerUserId = adminId,
			Priority = Priority.High,
			NeededFinish = futureDeadline,
		});
		var authCookie = await SignInAsync("browse-nav.worker");

		var pastResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={pastBranch.Id.Value}", authCookie);
		var pastBody = await ReadNormalizedBodyAsync(pastResponse);
		pastBody.Should().Contain("class=\"jt-overdue\">1 Jan 2020 12:00 (", "a deadline that has already passed should render red");
		pastBody.Should().Contain("overdue)</span>", "and should say how far past it the job now is");

		var futureResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={futureBranch.Id.Value}", authCookie);
		var futureBody = await ReadNormalizedBodyAsync(futureResponse);
		futureBody.Should().Contain("<span>1 Jan 2030 12:00 (", "a deadline still to come should not render red");
		futureBody.Should().NotContain("jt-overdue");
	}

	[Fact]
	public async Task Browsing_a_node_without_a_deadline_shows_only_its_priority()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		// The node detail card spells the priority out in full; the abbreviation is the table form only
		// (b5f57e6d). This test's own subject is the absent deadline, not which form the label takes.
		body.Should().Contain("class=\"jt-priority jt-priority--medium\">Medium</span>");
		body.Should().NotContain("(deadline");
	}

	[Fact]
	public async Task Browsing_the_root_hides_prerequisite_and_work_sections()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
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

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
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

		var dependentResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={dependentLeafId.Value}", authCookie);
		var dependentBody = await ReadNormalizedBodyAsync(dependentResponse);
		dependentBody.Should().Contain("Requires (must finish first)");
		dependentBody.Should().Contain($"href=\"/Jobs/Browse?nodeId={requiredLeafId.Value}\"");
		dependentBody.Should().Contain("Old survey");

		var requiredResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={requiredLeafId.Value}", authCookie);
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
		var browseResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var sessionCookie = WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(browseResponse, "JobTrack.Filters") ?? throw new InvalidOperationException("No session cookie was set."));

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
		var response = await client.GetAuthenticatedAsync("/Jobs/Browse?search=true", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={homeNodeId.Value}\">Browse</a>");
	}

	[Fact]
	public async Task Search_browse_button_falls_back_to_the_root_when_nothing_is_remembered_or_set()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		// A fresh session, no home node configured: the last-resort fallback is the root, i.e. a
		// plain Browse link carrying no node id at all.
		var response = await client.GetAuthenticatedAsync("/Jobs/Browse?search=true", authCookie);
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

		var response = await client.GetAuthenticatedAsync($"/Jobs/Prerequisites?nodeId={dependentLeafId.Value}", authCookie);
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

		var response = await client.GetAuthenticatedAsync($"/Jobs/Move?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={branchId.Value}\"");
	}

	[Fact]
	public async Task Decompose_page_links_the_decomposed_leafs_own_name_to_browse()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Decompose?leafNodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
	}

	[Fact]
	public async Task Delete_page_links_the_targeted_nodes_own_name_to_browse()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Delete?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
	}

	[Fact]
	public async Task CostReport_page_links_the_reported_nodes_own_name_to_browse()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "browse-nav.cost-viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("browse-nav.cost-viewer");

		var response = await client.GetAuthenticatedAsync($"/Jobs/CostReport?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\"");
	}

	[Fact]
	public async Task Work_page_titles_itself_Work_sessions_and_names_the_leaf_beside_a_back_link()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("<h1>Work sessions</h1>");
		body.Should().Contain($"<h2 class=\"jt-preserve-whitespace mb-0\">Pour foundation (ID {leafId.Value})</h2>");
		body.Should().Contain($"<a class=\"jt-value-aside\" href=\"/Jobs/Browse?nodeId={leafId.Value}\">Back</a>");
		body.Should().NotContain("jt-eyebrow", "the eyebrow kicker was removed project-wide -- a page shows one title, not two");
		body.Should().NotContain("Leaf work");
	}

	[Fact]
	public async Task Create_page_links_the_named_parent_to_browse()
	{
		var parentId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Create?parentId={parentId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={parentId.Value}\">Kitchen renovation (ID {parentId.Value})</a>");
	}

	[Fact]
	public async Task Edit_page_names_the_target_node_and_links_it_to_browse()
	{
		var nodeId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Edit?nodeId={nodeId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={nodeId.Value}\">Kitchen renovation (ID {nodeId.Value})</a>");
	}

	[Fact]
	public async Task Achievement_page_redirects_to_the_unified_work_page_for_the_same_leaf()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leafId,
			WorkedByUserId = adminId,
		});
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Achievement?jobNodeId={leafId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be($"/Jobs/Work?leafNodeId={leafId.Value}#status");
	}

	[Fact]
	public async Task Browse_leaf_toolbar_and_row_both_render_a_sessions_link_with_the_shared_icon()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var leafResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var leafBody = await ReadNormalizedBodyAsync(leafResponse);
		leafBody.Should().Contain($"href=\"/Jobs/Work?leafNodeId={leafId.Value}&amp;returnUrl=");
		leafBody.Should().Contain("#jt-icon-sessions");

		var rootResponse = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
		var rootBody = await ReadNormalizedBodyAsync(rootResponse);
		rootBody.Should().Contain($"href=\"/Jobs/Work?leafNodeId={leafId.Value}&amp;returnUrl=");
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
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
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			NodeId = nodeId,
		});

		_ = await seedClient.Jobs.ArchiveAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			NodeId = nodeId,
			Version = node.Node.Version,
		});
	}

	private async Task AddPrerequisiteAsync(JobNodeId requiredJobId, JobNodeId dependentJobId) =>
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			RequiredJobId = requiredJobId,
			DependentJobId = dependentJobId,
		});

	private async Task SetWorkerHomeNodeAsync(JobNodeId nodeId) =>
		await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			NodeId = nodeId,
		});

	private static async Task<string> ReadNormalizedBodyAsync(HttpResponseMessage response) =>
		WhitespaceRunPattern().Replace(await response.Content.ReadAsStringAsync(), " ");



	private Task<string> SignInAsync(string userName) => SignInAsync(userName, KnownPassword);

	private async Task<string> SignInAsync(string userName, string password)
	{
		var (antiforgeryCookie, token) = await client.GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = WebTestHttp.FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}



	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRunPattern();

	/// <summary>The record card's field labels, in document order -- every <c>dt</c> of the node detail list.</summary>
	[GeneratedRegex("<dt[^>]*>(?<label>[^<]+)</dt>")]
	private static partial Regex FieldLabelPattern();



	private Task<AppUserId> SeedWorkerEmployeeAsync(string userName) => IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, userName);
}
