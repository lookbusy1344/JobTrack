namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Schedules;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);

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
		var authCookie = await client.SignInAsync("browse.root");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
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
		var authCookie = await client.SignInAsync("browse.tree-icons");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
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
	/// Every subtree row names its node the same way as the rest of the app -- "Description (ID N)",
	/// via the shared JobNodeDisplay helper -- so a row can be matched back to a report, URL, or
	/// support ticket that only carries the id. Regression test for a row that rendered the bare,
	/// truncated description with no id suffix at all.
	/// </summary>
	public async Task Subtree_rows_name_each_node_with_its_id()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.row-id");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		var authCookie = await client.SignInAsync("browse.row-id");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain($"Kitchen renovation (ID {branchId.Value.ToString(CultureInfo.InvariantCulture)})");
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
		var authCookie = await client.SignInAsync("browse.span-bar");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
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
		var authCookie = await client.SignInAsync("browse.pill-glyph");

		var blockedResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={dependentLeafId.Value}", authCookie);
		var blockedBody = await blockedResponse.Content.ReadAsStringAsync();

		blockedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		blockedBody.Should().Contain("#jt-icon-stop");
		// The prerequisite's own marker is the glyph alone; its name survives for assistive tech.
		blockedBody.Should().Contain("status-pill--icon");
		blockedBody.Should().Contain("Blocking");

		var readyResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={requiredLeafId.Value}", authCookie);
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

		var authCookie = await client.SignInAsync("browse.achievement");
		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
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
	public async Task A_completed_branch_row_shows_the_same_green_tick_as_a_completed_leaf()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.branch-completion");
		var rootId = bootstrappedRootId!.Value;
		var completedBranchId = await AddChildAsync(rootId, workerId, "Completed branch");
		var completedLeafId = await AddChildAsync(completedBranchId, workerId, "Completed branch leaf");
		var unfinishedBranchId = await AddChildAsync(rootId, workerId, "Unfinished branch");
		var waitingLeafId = await AddChildAsync(unfinishedBranchId, workerId, "Waiting branch leaf");
		await SetAchievementAsync(completedLeafId, adminId, Achievement.Success);
		await AttachLeafWorkAsync(waitingLeafId, adminId);
		var authCookie = await client.SignInAsync("browse.branch-completion");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var completedBranchRow = ExtractSubtreeRow(body, completedBranchId);
		completedBranchRow.Should().Contain("#jt-icon-achievement-success");
		completedBranchRow.Should().Contain("status-pill-closed status-pill--icon");
		completedBranchRow.Should().Contain("status-pill-closed status-pill--compact\">Closed</span>");
		ExtractSubtreeRow(body, completedLeafId).Should().Contain("#jt-icon-achievement-success");
		var unfinishedBranchRow = ExtractSubtreeRow(body, unfinishedBranchId);
		unfinishedBranchRow.Should().NotContain("jt-achievement-icon");
		unfinishedBranchRow.Should().NotContain("status-pill-closed");
	}

	[Fact]
	public async Task Subtree_leaf_rows_distinguish_unstarted_and_unacknowledged_open_work()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.inactive-pills");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddChildAsync(rootId, workerId, "No work attached");
		var waitingId = await AddChildAsync(rootId, workerId, "Waiting without sessions");
		var pausedId = await AddChildAsync(rootId, workerId, "Previously worked");
		var requestId = await AddChildAsync(rootId, workerId, "Unacknowledged request");
		await AttachLeafWorkAsync(waitingId, adminId);
		await SetAchievementAsync(pausedId, adminId, Achievement.InProgress);
		await AddFinishedSessionAsync(
			workerId, pausedId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 10, 0));
		await AddUnacknowledgedRequestAsync(requestId, workerId, rootId);
		var authCookie = await client.SignInAsync("browse.inactive-pills");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(body.Split("status-pill status-pill-inactive status-pill--compact\">Unstarted</span>").Length - 1).Should()
																										   .Be(2, "both workless and Waiting leaves have never had a session");
		(body.Split("status-pill status-pill-unack status-pill--compact\">Unack</span>").Length - 1).Should()
																									.Be(1, "an unacknowledged request is the more specific open state");
		body.Should().Contain("status-pill-unack", "the request state has its own blue-tinted pill rather than the neutral Unstarted treatment");
		(body.Split("status-pill status-pill-paused status-pill--compact").Length - 1).Should()
																					  .Be(1, "a leaf with session history retains the existing paused state");
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
		var authCookie = await client.SignInAsync("browse.row-readiness");

		var unblockedBody = await (await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie)).Content.ReadAsStringAsync();
		unblockedBody.Should().NotContain("jt-tree-blocked", "nothing is blocked yet");

		var requiredLeafId = await AddChildAsync(rootId, workerId, "Order materials");
		await AddPrerequisiteAsync(requiredLeafId, branchId, adminId);

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().BeOneOf(HttpStatusCode.OK);
		// The branch and the leaf beneath it; the root and the blocker itself stay unmarked.
		BlockedRowPattern().Count(body).Should().Be(2);
	}

	[Fact]
	/// <summary>
	/// The stop palm means BLOCKED, not "declares a prerequisite": the building-a-house sample's
	/// Groundworks branch runs Site survey -> Excavate foundations -> Pour foundations, and with the
	/// first two closed as Success nothing under it is blocked. Browse evaluates readiness for the
	/// whole displayed subtree in one batch, where every required job is itself a displayed row --
	/// the case that previously marked each dependent blocked against a satisfied prerequisite.
	/// </summary>
	public async Task A_row_whose_prerequisite_is_satisfied_is_not_marked_blocked()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.satisfied-prerequisite");
		var rootId = bootstrappedRootId!.Value;
		var groundworksId = await AddChildAsync(rootId, workerId, "Groundworks");
		var siteSurveyId = await AddChildAsync(groundworksId, workerId, "Site survey");
		var excavateId = await AddChildAsync(groundworksId, workerId, "Excavate foundations");
		var pourId = await AddChildAsync(groundworksId, workerId, "Pour foundations");
		await AddPrerequisiteAsync(siteSurveyId, excavateId, adminId);
		await AddPrerequisiteAsync(excavateId, pourId, adminId);
		await SetAchievementAsync(siteSurveyId, adminId, Achievement.Success);
		await SetAchievementAsync(excavateId, adminId, Achievement.Success);
		await SetAchievementAsync(pourId, adminId, Achievement.InProgress);
		var authCookie = await client.SignInAsync("browse.satisfied-prerequisite");

		var body = await (await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={groundworksId.Value}", authCookie)).Content.ReadAsStringAsync();

		BlockedRowPattern().Count(body).Should().Be(0, "every declared prerequisite is satisfied");

		// The same tree with one genuinely unsatisfied prerequisite: only that row is marked.
		var lastId = await AddChildAsync(groundworksId, workerId, "Backfill");
		await AddPrerequisiteAsync(pourId, lastId, adminId);
		var blockedBody = await (await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={groundworksId.Value}", authCookie)).Content.ReadAsStringAsync();

		BlockedRowPattern().Count(blockedBody).Should().Be(1, "only the row awaiting an in-progress prerequisite is blocked");
	}

	[Fact]
	public async Task Browsing_a_branch_shows_its_children_and_a_breadcrumb_to_the_root()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.branch");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation");
		_ = await AddChildAsync(branchId, workerId, "Fit cabinets");
		var authCookie = await client.SignInAsync("browse.branch");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit cabinets");
		body.Should().Contain("Root");
	}

	[Fact]
	public async Task Browsing_a_node_shows_its_full_priority_name_in_the_detail_fields_but_abbreviated_in_the_subtree_table()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.priority-form");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Priority branch");
		_ = await AddChildAsync(branchId, workerId, "Priority child");
		var authCookie = await client.SignInAsync("browse.priority-form");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(">Medium</span>", "the detail fields have room to spell the priority out in full");
		body.Should().Contain(">Med<", "the subtree table column stays abbreviated");
	}

	[Fact]
	/// <summary>
	///     The subtree's own Deadline column follows the same overdue rule (InstantDisplay.IsPast,
	///     jt-overdue) as Browse's record-view deadline and AwaitingProgress's Due column.
	/// </summary>
	public async Task The_subtree_deadline_column_renders_red_only_once_the_deadline_has_passed()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.subtree-deadline");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Subtree deadline branch");
		_ = await AddChildWithDeadlineAsync(branchId, workerId, "Overdue child", Instant.FromUtc(2020, 1, 1, 12, 0));
		_ = await AddChildWithDeadlineAsync(branchId, workerId, "Future child", Instant.FromUtc(2030, 1, 1, 12, 0));
		var authCookie = await client.SignInAsync("browse.subtree-deadline");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("class=\"jt-overdue\">1 Jan</span>", "a deadline that has already passed should render red");
		body.Should().Contain("<span>1 Jan</span>", "a deadline still to come should not render red");
	}

	[Fact]
	/// <summary>
	///     A passed deadline remains an alarm only while that particular subtree row is open. Leaves
	///     close on any terminal leaf achievement, while branches close on their distinct recursive
	///     branch achievement once every leaf beneath them has succeeded.
	/// </summary>
	public async Task The_subtree_deadline_column_does_not_render_closed_leaf_or_branch_deadlines_red()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.subtree-closed-deadline");
		var rootId = bootstrappedRootId!.Value;
		var parentId = await AddChildAsync(rootId, workerId, "Deadline parent");
		var closedLeafId = await AddChildWithDeadlineAsync(
			parentId, workerId, "Closed leaf", Instant.FromUtc(2020, 1, 1, 12, 0));
		var closedBranchId = await AddChildWithDeadlineAsync(
			parentId, workerId, "Closed branch", Instant.FromUtc(2020, 1, 2, 12, 0));
		var branchLeafId = await AddChildAsync(closedBranchId, workerId, "Closed branch leaf");
		var openLeafId = await AddChildWithDeadlineAsync(
			parentId, workerId, "Open leaf", Instant.FromUtc(2020, 1, 3, 12, 0));
		await SetAchievementAsync(closedLeafId, adminId, Achievement.Cancelled);
		await SetAchievementAsync(branchLeafId, adminId, Achievement.Success);
		var authCookie = await client.SignInAsync("browse.subtree-closed-deadline");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={parentId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		ExtractSubtreeRow(body, closedLeafId).Should().NotContain("jt-overdue");
		ExtractSubtreeRow(body, closedBranchId).Should().NotContain("jt-overdue");
		ExtractSubtreeRow(body, openLeafId).Should().Contain("jt-overdue");
	}

	[Fact]
	/// <summary>
	///     The record card's Deadline field turns red on a branch as well as a leaf: a branch whose
	///     subtree has not all succeeded is still open, so its own missed deadline is still live.
	/// </summary>
	public async Task A_passed_deadline_renders_red_on_an_open_branch()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.branch-overdue");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildWithDeadlineAsync(rootId, workerId, "Overdue branch", Instant.FromUtc(2020, 1, 1, 12, 0));
		_ = await AddChildAsync(branchId, workerId, "Unfinished child");
		var authCookie = await client.SignInAsync("browse.branch-overdue");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		// How far past the deadline is grows with the calendar -- and which unit (days/weeks) it's
		// expressed in grows with it too -- so only its shape is asserted here. InstantDisplayDeadlineTests
		// owns the arithmetic and the unit thresholds.
		body.Should().Contain("jt-overdue\">1 Jan 2020 12:00 (", "the branch has not finished, so its missed deadline is still an alarm");
		body.Should().Contain("overdue)</span>", "an open job says how far past its deadline it is");
	}

	[Fact]
	/// <summary>
	///     Red is reserved for a job still open. Once a leaf has ended -- here successfully -- the
	///     deadline it missed is a matter of record and renders like any other.
	/// </summary>
	public async Task A_passed_deadline_on_a_closed_leaf_does_not_render_red()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.closed-overdue");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildWithDeadlineAsync(rootId, workerId, "Late but done", Instant.FromUtc(2020, 1, 1, 12, 0));
		await SetAchievementAsync(leafId, adminId, Achievement.Success);
		var authCookie = await client.SignInAsync("browse.closed-overdue");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain(">1 Jan 2020 12:00</span>");
		body.Should().NotContain("jt-overdue");
	}

	[Fact]
	public async Task Browsing_a_direct_child_of_root_shows_root_once_in_the_breadcrumb()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.breadcrumb");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Networking");
		var authCookie = await client.SignInAsync("browse.breadcrumb");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
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
		var authCookie = await client.SignInAsync("browse.search");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse?searchText=oak", authCookie);
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
		var authCookie = await client.SignInAsync("browse.archive");

		var activeOnlyResponse = await client.GetAuthenticatedAsync("/Jobs/Browse", authCookie);
		var activeOnlyBody = await activeOnlyResponse.Content.ReadAsStringAsync();
		activeOnlyBody.Should().NotContain("Decommissioned wing");

		var allResponse = await client.GetAuthenticatedAsync("/Jobs/Browse?showArchived=true", authCookie);
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
		var authCookie = await client.SignInAsync("browse.readiness");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={dependentLeafId.Value}", authCookie);
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
		var authCookie = await client.SignInAsync("browse.inherited");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Blocked");
		body.Should().Contain("Inherited blockers");
		body.Should().Contain("Order materials");
		body.Should().Contain("Kitchen renovation");
		body.Should().Contain("class=\"row g-3 align-items-center\"");
		body.Should().Contain("class=\"col-12 col-md-6 d-flex align-items-center gap-3\"");
		body.Should().NotContain("jt-blocker-row");
	}

	[Fact]
	public async Task A_workless_childless_node_shows_one_create_child_action()
	{
		var (_, workerId) = await BootstrapAndSeedWorkerAsync("browse.create-child");
		var rootId = bootstrappedRootId!.Value;
		var childlessId = await AddChildAsync(rootId, workerId, "Empty planning node");
		var authCookie = await client.SignInAsync("browse.create-child");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={childlessId.Value}", authCookie);
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
		var authCookie = await client.SignInAsync("browse.cost");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("<dt class=\"col-12 col-sm-4\">Cost</dt>");
		body.Should().Contain(">&#xA3;200.00 /&#xA0;8.0 hrs<");
		body.Should().NotContain("Subtree cost");
	}

	/// <summary>
	///     A branch's record card runs Kind, Owner, Priority, Cost. A leaf's carries Active as well,
	///     and putting it before Cost moved Cost into a different grid cell on the two kinds of node —
	///     the one field a reader scans between siblings jumping position as they browse. Active is the
	///     transient fact, so it follows the standing ones rather than displacing them.
	/// </summary>
	[Fact]
	public async Task A_leafs_record_card_keeps_cost_in_the_same_position_a_branchs_does()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.fieldorder");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, adminId, "Ordered branch");
		var leafId = await AddChildAsync(branchId, workerId, "Ordered leaf");
		await AttachLeafWorkAsync(leafId, adminId);
		await AddWorkingWindowAsync(workerId, adminId);
		await AddUserCostRateAsync(workerId, adminId, 25m);
		await AddActiveSessionAsync(workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0));
		var authCookie = await client.SignInAsync("browse.fieldorder");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var costPosition = body.IndexOf("<dt class=\"col-12 col-sm-4\">Cost</dt>", StringComparison.Ordinal);
		var achievementPosition = body.IndexOf("<dt class=\"col-12 col-sm-4\">Achievement</dt>", StringComparison.Ordinal);
		var readinessPosition = body.IndexOf("<dt class=\"col-12 col-sm-4\">Readiness</dt>", StringComparison.Ordinal);
		var activePosition = body.IndexOf("<dt class=\"col-12 col-sm-4\">Active</dt>", StringComparison.Ordinal);

		costPosition.Should().BePositive("the leaf is costed, so its record card carries the field");
		achievementPosition.Should().BePositive("a leaf carrying work always has an achievement");
		readinessPosition.Should().BePositive("readiness is reported for every node, branch or leaf");
		activePosition.Should().BePositive("the leaf has a running session, so its record card carries the field");
		costPosition.Should().BeLessThan(achievementPosition, "Cost holds a branch's fourth field slot, so it must hold a leaf's too");
		achievementPosition.Should().BeLessThan(readinessPosition, "the three status fields run Achievement, Readiness, Active");
		readinessPosition.Should().BeLessThan(activePosition, "Active is leaf-only, so it comes last of the three and displaces neither");
	}

	/// <summary>
	///     The record card states a zero cost as the figure, while the subtree table below keeps the
	///     dash: a labelled field on one node reads unambiguously as nil, where a &#163;0.00 among a
	///     column of real amounts reads as "nothing recorded here". Browsing the parent branch shows
	///     both renderings of the same zero-costed leaf at once.
	/// </summary>
	[Fact]
	public async Task A_zero_cost_reads_as_a_figure_in_the_record_card_and_a_dash_in_the_subtree_table()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.zero-cost");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Zero-cost branch");
		var leafId = await AddChildAsync(branchId, workerId, "Zero-cost leaf");
		await AttachLeafWorkAsync(leafId, adminId);
		await AddWorkingWindowAsync(workerId, adminId);
		await AddUserCostRateAsync(workerId, adminId, 0m);
		await AddFinishedSessionAsync(workerId, leafId, Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 17, 0));
		var authCookie = await client.SignInAsync("browse.zero-cost");

		var leafResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var leafBody = await leafResponse.Content.ReadAsStringAsync();
		var branchResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var branchBody = await branchResponse.Content.ReadAsStringAsync();

		leafResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		leafBody.Should().Contain(">&#xA3;0.00 /&#xA0;8.0 hrs<", "the card's Cost field states the figure, zero or not");
		branchBody.Should().MatchRegex(ZeroCostTableCellPattern(), "the subtree table stands the same zero down to a dash");
	}

	/// <summary>
	///     A node with neither children nor work attached rendered nothing at all below its record card
	///     — the one place a reader is told what a job holds was silently empty, which reads as a page
	///     that failed to load rather than as a job nothing has been done to yet. It now says what the
	///     two ways forward are, in the same white panel the subtree table and sessions list occupy.
	/// </summary>
	[Fact]
	public async Task A_node_with_neither_children_nor_work_says_what_can_be_added_to_it()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("browse.empty-node");
		var rootId = bootstrappedRootId!.Value;
		var emptyId = await AddChildAsync(rootId, workerId, "Nothing here yet");
		var workedId = await AddChildAsync(rootId, workerId, "Has sessions");
		await AttachLeafWorkAsync(workedId, adminId);
		var authCookie = await client.SignInAsync("browse.empty-node");

		var emptyBody = await (await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={emptyId.Value}", authCookie)).Content.ReadAsStringAsync();
		var workedBody = await (await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={workedId.Value}", authCookie)).Content.ReadAsStringAsync();
		var parentBody = await (await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie)).Content.ReadAsStringAsync();

		emptyBody.Should().Contain(
			"<div class=\"jt-card text-center\">",
			"a div, not a p: the shared p rule caps prose at its reading measure, which would leave the panel short of the full width the table it replaces occupies");
		emptyBody.Should().Contain(
			$"<a href=\"/Jobs/Create?parentId={emptyId.Value}\">child job</a>",
			"both ways forward act, rather than describing an action to find elsewhere -- and /Jobs/Create defaults the new node's owner to the viewer");
		emptyBody.Should().Contain(
			"<button type=\"submit\" class=\"btn btn-link p-0 align-baseline fw-normal\">session</button>",
			"only the noun is the control, and fw-normal cancels .btn's 600 weight so it doesn't read heavier than the sentence it sits in");
		emptyBody.Should().Contain(
			"</form>, or create a",
			"the inline form abuts its comma: a line break either side of it renders as whitespace, which showed as \"session ,\"");
		emptyBody.Should().Contain($"<input type=\"hidden\" name=\"leafNodeId\" value=\"{emptyId.Value}\"",
			"that post names this node as the leaf to start");
		workedBody.Should().NotContain("Start a work session</button>", "the sessions list stands in its place, empty or not");
		parentBody.Should().NotContain("Start a work session</button>", "the subtree table stands in its place");
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
		var authCookie = await client.SignInAsync("browse.branch-cost");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(
			$"href=\"/Jobs/Browse?nodeId={leafId.Value}\">Costed leaf (ID {leafId.Value.ToString(CultureInfo.InvariantCulture)})</a>");
		body.Should().Contain(">&#xA3;200.00 /&#xA0;8.0 hrs<");
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
		var authCookie = await client.SignInAsync("browse.search-cost");

		var response = await client.GetAuthenticatedAsync("/Jobs/Browse?searchText=oak", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit oak cabinets");
		// Unlike the leaf/branch detail views, the search results table renders the cost directly
		// inside the table cell rather than inside a wrapping <span>, so this isn't tag-delimited.
		body.Should().Contain("&#xA3;200.00 /&#xA0;8.0 hrs");
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
			Context = new() {
				Actor = bootstrappedAdminId!.Value,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<JobNodeId> AddChildWithDeadlineAsync(JobNodeId parentId, AppUserId ownerId, string description, Instant neededFinish)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = bootstrappedAdminId!.Value,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
			NeededFinish = neededFinish,
		});

		return result.Id;
	}

	private async Task ArchiveAsync(JobNodeId nodeId, AppUserId adminId)
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

	private async Task SetAchievementAsync(JobNodeId leafId, AppUserId adminId, Achievement achievement)
	{
		var leafWork = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leafId,
		});

		// Achievement moves forward one step at a time (ADR 0001), so a terminal state is reached
		// through InProgress rather than jumped to.
		var version = leafWork.Version;
		if (achievement != Achievement.InProgress) {
			var inProgress = await seedClient.Work.SetAchievementAsync(new() {
				Context = new() {
					Actor = adminId,
					CorrelationId = Guid.NewGuid(),
				},
				JobNodeId = leafId,
				NewAchievement = Achievement.InProgress,
				Reason = "Work has started",
				Version = version,
			});
			version = inProgress.Version;
		}

		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leafId,
			NewAchievement = achievement,
			Reason = "Seeded for the achievement-glyph test",
			Version = version,
		});
	}

	private async Task AttachLeafWorkAsync(JobNodeId leafId, AppUserId adminId) =>
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leafId,
		});

	private async Task AddWorkingWindowAsync(AppUserId workerId, AppUserId adminId) =>
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 18, 0)),
				null),
			Reason = "Full working window for browse cost test",
		});

	private async Task AddUserCostRateAsync(AppUserId workerId, AppUserId adminId, decimal amountPerHour) =>
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Rate = new(new(amountPerHour), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

	private async Task AddFinishedSessionAsync(
		AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = startedAt,
		});

		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = finishedAt,
		});
	}

	private async Task AddActiveSessionAsync(AppUserId workerId, JobNodeId leafId, Instant startedAt) =>
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = startedAt,
		});

	private async Task AddUnacknowledgedRequestAsync(JobNodeId nodeId, AppUserId requesterId, JobNodeId holdingAreaNodeId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var holdingArea = connection.CreateCommand();
		holdingArea.CommandText = """
								  INSERT INTO request_holding_area (job_node_id, name, default_priority_id, is_active)
								  VALUES ($jobNodeId, 'Browse test intake', $priorityId, 1);
								  SELECT last_insert_rowid();
								  """;
		_ = holdingArea.Parameters.AddWithValue("$jobNodeId", holdingAreaNodeId.Value);
		_ = holdingArea.Parameters.AddWithValue("$priorityId", (short)Priority.Medium);
		var holdingAreaId = (long)(await holdingArea.ExecuteScalarAsync())!;

		await using var request = connection.CreateCommand();
		request.CommandText = """
							  INSERT INTO job_request (job_node_id, requester_user_id, holding_area_id, submitted_at)
							  VALUES ($jobNodeId, $requesterUserId, $holdingAreaId, $submittedAt);
							  """;
		_ = request.Parameters.AddWithValue("$jobNodeId", nodeId.Value);
		_ = request.Parameters.AddWithValue("$requesterUserId", requesterId.Value);
		_ = request.Parameters.AddWithValue("$holdingAreaId", holdingAreaId);
		_ = request.Parameters.AddWithValue("$submittedAt", Instant.FromUtc(2026, 1, 1, 8, 0).ToUnixTimeTicks());
		_ = await request.ExecuteNonQueryAsync();
	}

	private async Task AddPrerequisiteAsync(JobNodeId requiredJobId, JobNodeId dependentJobId, AppUserId adminId) =>
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() {
				Actor = adminId,
				CorrelationId = Guid.NewGuid(),
			},
			RequiredJobId = requiredJobId,
			DependentJobId = dependentJobId,
		});







	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("""jt-tree-blocked""")]
	private static partial Regex BlockedRowPattern();

	// The cost cell's own placeholder, not a stray hyphen elsewhere in the markup: the cell renders its
	// text on its own line, so the dash arrives surrounded by the Razor block's indentation.
	[GeneratedRegex("""jt-col-cost[^>]*>\s*-\s*<""")]
	private static partial Regex ZeroCostTableCellPattern();

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

	private static string ExtractSubtreeRow(string body, JobNodeId nodeId)
	{
		const string RowStart = "<tr>";
		const string RowEnd = "</tr>";
		var nodeMarker = $"(ID {nodeId.Value.ToString(CultureInfo.InvariantCulture)})";
		var markerIndex = body.IndexOf(nodeMarker, StringComparison.Ordinal);
		if (markerIndex < 0) {
			throw new InvalidOperationException($"No subtree row found for job node {nodeId.Value}.");
		}

		var rowStartIndex = body.LastIndexOf(RowStart, markerIndex, StringComparison.Ordinal);
		var rowEndIndex = body.IndexOf(RowEnd, markerIndex, StringComparison.Ordinal);
		if (rowStartIndex < 0 || rowEndIndex < 0) {
			throw new InvalidOperationException($"Incomplete subtree row found for job node {nodeId.Value}.");
		}

		return body[rowStartIndex..(rowEndIndex + RowEnd.Length)];
	}

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
}
