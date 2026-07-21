namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Domain.Schedules;
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
///     Direct-HTTP tests for job-tree browsing, search, ownership/archive filters, and readiness
///     explanations (plan §8.5 slice 2) — the first web page with no per-role authorization policy,
///     since viewing job data is an unqualified baseline capability for every role (spec §7.3).
/// </summary>
public sealed partial class JobTreeBrowsingTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId? bootstrappedAdminId;

	private JobNodeId? bootstrappedRootId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);

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
	/// <summary>
	/// Superseded by the multi-level Browse subtree (ADR 0039, 2026-07-15 plan Stage 5): the page
	/// now renders the bounded subtree (default +3 levels), so a grandchild within that bound
	/// deliberately does appear -- this asserts the depth bound itself by seeding one level beyond
	/// the default and confirming it does not render, rather than asserting "no grandchildren" at all.
	/// </summary>
	public async Task Browsing_the_root_lists_the_bounded_subtree_but_not_beyond_the_default_depth()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.root");
		var rootId = bootstrappedRootId!.Value;
		// Depth 0 (root) .. depth 4 (fifth level): the default max depth is 3, so depth 4 must not render.
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		var leafId = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var subStepId = await AddChildAsync(leafId, workerId, "Fit cabinets sub-step");
		_ = await AddChildAsync(subStepId, workerId, "Beyond the default depth");
		var authCookie = await SignInAsync("browse.root");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Kitchen renovation");
		body.Should().Contain("Fit cabinets sub-step");
		body.Should().NotContain("Beyond the default depth");
	}

	[Fact]
	/// <summary>
	/// The subtree reads as a file-manager listing: every descendant row is indented by its own
	/// depth and prefixed with an icon naming its kind (folder for a branch, leaf for a leaf), so a
	/// layer of nesting is legible from the row itself rather than by counting rows.
	/// </summary>
	public async Task Subtree_rows_carry_a_depth_indent_and_a_kind_icon()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.tree-icons");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("browse.tree-icons");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		// The branch sits one level below the browsed root, its child two.
		body.Should().Contain("data-jt-depth=\"1\"");
		body.Should().Contain("data-jt-depth=\"2\"");
		// Both kinds are drawn, from the one sprite the page defines.
		body.Should().Contain("#jt-icon-branch");
		body.Should().Contain("#jt-icon-leaf");
	}

	[Fact]
	/// <summary>
	/// The page's CSP is `style-src 'self'` with no `'unsafe-inline'`, so a `style` attribute is
	/// dropped by the browser and whatever it positioned silently renders at zero size — which is
	/// exactly what happened to the subtree span bar. Geometry that varies per row is therefore
	/// carried by SVG presentation attributes, which the CSP does not police, and no page under
	/// this host may reintroduce an inline style.
	/// </summary>
	public async Task The_subtree_span_bar_carries_its_geometry_without_an_inline_style_attribute()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.span-bar");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("browse.span-bar");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("style=\"", "the Content-Security-Policy drops inline styles");
		body.Should().Contain("jt-tree-span-fill");
	}

	[Fact]
	/// <summary>
	/// Readiness reads as a traffic light: a stop glyph when blocked, a go glyph when ready. In a
	/// list or a table the glyph stands alone with its name visually hidden, so a per-row state
	/// costs a glyph's width rather than a word — the pill still names itself to a screen reader.
	/// </summary>
	public async Task Readiness_is_shown_with_a_stop_or_go_glyph_rather_than_a_word_per_row()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.pill-glyph");
		var rootId = bootstrappedRootId!.Value;
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		await AddPrerequisiteAsync(requiredLeafId, dependentLeafId, adminId);
		var authCookie = await SignInAsync("browse.pill-glyph");

		var blockedResponse = await GetAsync($"/Jobs/Browse?nodeId={dependentLeafId.Value}", authCookie);
		var blockedBody = await blockedResponse.Content.ReadAsStringAsync();

		blockedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		blockedBody.Should().Contain("#jt-icon-stop");
		// The prerequisite's own marker is the glyph alone; its name survives for assistive tech.
		blockedBody.Should().Contain("status-pill--icon");
		blockedBody.Should().Contain("Blocking");

		var readyResponse = await GetAsync($"/Jobs/Browse?nodeId={requiredLeafId.Value}", authCookie);
		var readyBody = await readyResponse.Content.ReadAsStringAsync();

		readyBody.Should().Contain("#jt-icon-go");
	}

	[Fact]
	/// <summary>
	/// A job's achievement reads as a glyph per row, drawn from one family of signs, so scanning a
	/// subtree for what is done/underway/closed costs no reading. Cancelled and Unsuccessful share
	/// one "closed unfinished" glyph, with the specific word carried by the accessible label. A leaf
	/// with no leaf work attached carries no glyph at all — that is the absence of a state, not a
	/// sixth one.
	/// </summary>
	public async Task Subtree_rows_show_each_leafs_achievement_as_a_glyph()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.achievement");
		var rootId = bootstrappedRootId!.Value;
		var waitingLeafId = await AddChildAsync(rootId, workerId, "Waiting leaf");
		var inProgressLeafId = await AddChildAsync(rootId, workerId, "In progress leaf");
		var successLeafId = await AddChildAsync(rootId, workerId, "Success leaf");
		var cancelledLeafId = await AddChildAsync(rootId, workerId, "Cancelled leaf");
		_ = await AddChildAsync(rootId, workerId, "No work attached leaf");

		await AttachLeafWorkAsync(waitingLeafId, adminId);
		await SetAchievementAsync(inProgressLeafId, adminId, Achievement.InProgress);
		await SetAchievementAsync(successLeafId, adminId, Achievement.Success);
		await SetAchievementAsync(cancelledLeafId, adminId, Achievement.Cancelled);

		var authCookie = await SignInAsync("browse.achievement");
		var response = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("#jt-icon-achievement-waiting");
		body.Should().Contain("#jt-icon-achievement-in-progress");
		body.Should().Contain("#jt-icon-achievement-success");
		body.Should().Contain("#jt-icon-achievement-closed");

		// Colour never carries the state alone: each glyph is aria-hidden and named in text.
		body.Should().Contain("Cancelled");
		body.Should().Contain("In Progress");
	}

	[Fact]
	/// <summary>
	/// ADR 0043: a subtree row blocked by a prerequisite carries the stop glyph, and a prerequisite
	/// declared on a branch gates every descendant of it. Ready rows carry nothing — in a healthy
	/// tree nearly every row is ready, so a sign on each would bury the few that matter.
	/// </summary>
	public async Task A_blocked_subtree_row_is_marked_and_its_descendants_inherit_the_block()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.row-readiness");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("browse.row-readiness");

		var unblockedBody = await (await GetAsync("/Jobs/Browse", authCookie)).Content.ReadAsStringAsync();
		unblockedBody.Should().NotContain("jt-tree-blocked", "nothing is blocked yet");

		var requiredLeafId = await AddChildAsync(rootId, workerId, "Order materials");
		await AddPrerequisiteAsync(requiredLeafId, branchId, adminId);

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().BeOneOf(HttpStatusCode.OK);
		// The branch and the leaf beneath it; the root and the blocker itself stay unmarked.
		BlockedRowPattern().Count(body).Should().Be(2);
	}

	[Fact]
	public async Task Browsing_a_branch_shows_its_children_and_a_breadcrumb_to_the_root()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.branch");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("browse.branch");

		var response = await GetAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit cabinets");
		body.Should().Contain("Root");
	}

	[Fact]
	public async Task Browsing_a_direct_child_of_root_shows_root_once_in_the_breadcrumb()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.breadcrumb");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Networking");
		var authCookie = await SignInAsync("browse.breadcrumb");

		var response = await GetAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var breadcrumb = ExtractBreadcrumbHtml(body);
		RootCrumbPattern().Count(breadcrumb).Should().Be(1);
		breadcrumb.Should().Contain("Networking");
	}

	[Fact]
	public async Task Searching_finds_a_matching_descendant_regardless_of_its_parent()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.search");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, workerId, "Fit oak cabinets");
		var authCookie = await SignInAsync("browse.search");

		var response = await GetAsync("/Jobs/Browse?searchText=oak", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit oak cabinets");
	}

	[Fact]
	public async Task The_default_archive_filter_hides_an_archived_child_and_All_reveals_it()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.archive");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Decommissioned wing");
		await ArchiveAsync(branchId, adminId);
		var authCookie = await SignInAsync("browse.archive");

		var activeOnlyResponse = await GetAsync("/Jobs/Browse", authCookie);
		var activeOnlyBody = await activeOnlyResponse.Content.ReadAsStringAsync();
		activeOnlyBody.Should().NotContain("Decommissioned wing");

		var allResponse = await GetAsync("/Jobs/Browse?archiveFilter=All", authCookie);
		var allBody = await allResponse.Content.ReadAsStringAsync();
		allBody.Should().Contain("Decommissioned wing");
		// Archived is a flag on the row itself, not a column of "no" against every other row.
		allBody.Should().Contain("#jt-icon-archived");
		activeOnlyBody.Should().NotContain("#jt-icon-archived");
	}

	[Fact]
	public async Task An_unsatisfied_prerequisite_is_shown_as_a_blocking_marker()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.readiness");
		var rootId = bootstrappedRootId!.Value;
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		await AddPrerequisiteAsync(requiredLeafId, dependentLeafId, adminId);
		var authCookie = await SignInAsync("browse.readiness");

		var response = await GetAsync($"/Jobs/Browse?nodeId={dependentLeafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		// The readiness field is now a single pill in the node's detail list, and the blocking
		// prerequisite is flagged in-place in the Requires list (by its title, not a bare id).
		body.Should().Contain("Blocked");
		body.Should().Contain("Blocking");
		body.Should().Contain("Pour foundation");
	}

	[Fact]
	public async Task A_prerequisite_declared_on_an_ancestor_is_itemised_as_an_inherited_blocker()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.inherited");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		var leafId = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var requiredLeafId = await AddChildAsync(rootId, workerId, "Order materials");
		// Prerequisite is declared on the BRANCH (an ancestor of the leaf), not on the leaf itself, so
		// it can only surface via readiness's ancestor aggregation, never the leaf's own Requires edges.
		await AddPrerequisiteAsync(requiredLeafId, branchId, adminId);
		var authCookie = await SignInAsync("browse.inherited");

		var response = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Blocked");
		body.Should().Contain("Inherited blockers");
		body.Should().Contain("Order materials");
		body.Should().Contain("Kitchen renovation");
	}

	[Fact]
	public async Task A_workless_childless_node_shows_one_create_child_action()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.create-child");
		var rootId = bootstrappedRootId!.Value;
		var childlessId = await AddChildAsync(rootId, workerId, "Empty planning node");
		var authCookie = await SignInAsync("browse.create-child");

		var response = await GetAsync($"/Jobs/Browse?nodeId={childlessId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Create child");
		body.Should().NotContain("New branch");
		body.Should().NotContain("New leaf");
		body.Should().Contain($"href=\"/Jobs/Create?parentId={childlessId.Value}\"");
	}

	[Fact]
	public async Task Browsing_a_costed_node_shows_cost_in_the_main_detail_fields_without_a_separate_subtree_metric_card()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.cost");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, adminId, "Costed branch");
		var leafId = await AddChildAsync(branchId, workerId, "Costed leaf");
		await AttachLeafWorkAsync(leafId, adminId);
		await AddWorkingWindowAsync(workerId, adminId);
		await AddUserCostRateAsync(workerId, adminId, 25m);
		await AddFinishedSessionAsync(workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 17, 0));
		var authCookie = await SignInAsync("browse.cost");

		var response = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("<dt class=\"col-sm-3\">Cost</dt>");
		body.Should().Contain(">&#xA3;200.00<");
		body.Should().NotContain("Subtree cost");
	}

	[Fact]
	public async Task Browsing_a_branch_shows_subtree_row_costs_in_sterling()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.branch-cost");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Costed branch");
		var leafId = await AddChildAsync(branchId, workerId, "Costed leaf");
		await AttachLeafWorkAsync(leafId, adminId);
		await AddWorkingWindowAsync(workerId, adminId);
		await AddUserCostRateAsync(workerId, adminId, 25m);
		await AddFinishedSessionAsync(workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 17, 0));
		var authCookie = await SignInAsync("browse.branch-cost");

		var response = await GetAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={leafId.Value}\">Costed leaf</a>");
		body.Should().Contain(">&#xA3;200.00<");
	}

	[Fact]
	public async Task Searching_shows_summary_costs_in_sterling()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.search-cost");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		var leafId = await AddChildAsync(branchId, workerId, "Fit oak cabinets");
		await AttachLeafWorkAsync(leafId, adminId);
		await AddWorkingWindowAsync(workerId, adminId);
		await AddUserCostRateAsync(workerId, adminId, 25m);
		await AddFinishedSessionAsync(workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 17, 0));
		var authCookie = await SignInAsync("browse.search-cost");

		var response = await GetAsync("/Jobs/Browse?searchText=oak", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit oak cabinets");
		// Unlike the leaf/branch detail views, the search results table renders the cost directly
		// inside the table cell rather than inside a wrapping <span>, so this isn't tag-delimited.
		body.Should().Contain("&#xA3;200.00");
	}

	[Fact]
	public async Task An_unauthenticated_request_is_redirected_to_sign_in()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/Browse");
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	private async Task<(AppUserId AdministratorId, AppUserId WorkerId)> BootstrapAndSeedWorkerAsync(string workerUserName)
	{
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = $"admin.{workerUserName}",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});

		bootstrappedRootId = bootstrapResult.RootJobNodeId;
		bootstrappedAdminId = bootstrapResult.AdministratorId;

		var workerId = await SeedWorkerEmployeeAsync(workerUserName);

		return (bootstrapResult.AdministratorId, workerId);
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = bootstrappedAdminId!.Value, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task ArchiveAsync(JobNodeId nodeId, AppUserId adminId)
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

	private async Task SetAchievementAsync(JobNodeId leafId, AppUserId adminId, Achievement achievement)
	{
		var leafWork = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
		});

		// Achievement moves forward one step at a time (ADR 0001), so a terminal state is reached
		// through InProgress rather than jumped to.
		var version = leafWork.Version;
		if (achievement != Achievement.InProgress) {
			var inProgress = await seedClient.Work.SetAchievementAsync(new() {
				Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
				JobNodeId = leafId,
				NewAchievement = Achievement.InProgress,
				Reason = "Work has started",
				Version = version,
			});
			version = inProgress.Version;
		}

		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			NewAchievement = achievement,
			Reason = "Seeded for the achievement-glyph test",
			Version = version,
		});
	}

	private async Task AttachLeafWorkAsync(JobNodeId leafId, AppUserId adminId) =>
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
		});

	private async Task AddWorkingWindowAsync(AppUserId workerId, AppUserId adminId) =>
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 18, 0)),
				null),
			Reason = "Full working window for browse cost test",
		});

	private async Task AddUserCostRateAsync(AppUserId workerId, AppUserId adminId, decimal amountPerHour) =>
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Rate = new(new(amountPerHour), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

	private async Task AddFinishedSessionAsync(
		AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = startedAt,
		});

		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = finishedAt,
		});
	}

	private async Task AddPrerequisiteAsync(JobNodeId requiredJobId, JobNodeId dependentJobId, AppUserId adminId) =>
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredJobId,
			DependentJobId = dependentJobId,
		});

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
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

	[GeneratedRegex("""jt-tree-blocked""")]
	private static partial Regex BlockedRowPattern();

	// The root renders uniquely as plain "Root" (no "(ID N)" suffix) wherever its NodeKind is known —
	// see JobNodeDisplay.Title.
	[GeneratedRegex(""">Root<""")]
	private static partial Regex RootCrumbPattern();

	[GeneratedRegex("""aria-label="breadcrumb"[^>]*>.*?</nav>""", RegexOptions.Singleline)]
	private static partial Regex BreadcrumbNavPattern();

	private static string ExtractBreadcrumbHtml(string body) =>
		BreadcrumbNavPattern().Match(body) is { Success: true } match
			? match.Value
			: throw new InvalidOperationException("No breadcrumb nav found in page body.");

	private async Task<AppUserId> SeedWorkerEmployeeAsync(string userName)
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
