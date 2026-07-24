namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
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
///     Direct-HTTP tests for <c>/Jobs/Browse</c>'s work controls: an inline one-click Start (recording
///     work is the app's most common action, so it does not require navigating to <c>/Jobs/Work</c>
///     first), the embedded Sessions history/Finish/Correct panel on a leaf's own detail view (ADR
///     0046), and the always-present "Sessions" link that still routes to <c>/Jobs/Work</c> for the
///     achievement-changing actions (Complete, Reopen, Cancel, Mark unsuccessful) the embedded panel
///     does not expose.
/// </summary>
public sealed partial class BrowseWorkSessionTests : IAsyncLifetime, IDisposable
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
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.browse-work-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;
		administratorId = bootstrapResult.AdministratorId;

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
	public async Task A_worker_can_start_a_session_inline_from_the_browse_row()
	{
		var workerId = await SeedEmployeeAsync("browse.starter", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Pour foundation");
		var authCookie = await SignInAsync("browse.starter");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");
		body.Should().Contain("Active since");
	}

	[Fact]
	/// <summary>
	/// The active-session pill has its own column rather than sharing the actions cell, where it
	/// pushed the start/finish buttons out of vertical alignment with every other row. It takes the
	/// slot Priority used to hold.
	/// </summary>
	public async Task The_active_session_pill_has_its_own_column_in_place_of_priority()
	{
		var workerId = await SeedEmployeeAsync("browse.activecolumn", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Screed floor");
		var authCookie = await SignInAsync("browse.activecolumn");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();

		// jt-col-active, not jt-col-secondary: which jobs are being worked on right now survives the
		// 992px cut that drops owner/priority, and goes only at phone width.
		body.Should().Contain("<th scope=\"col\" class=\"jt-col-active\">Active</th>");
		body.Should().NotContain(">Priority</th>");
	}

	[Fact]
	/// <summary>
	/// The row pill is a stopwatch and a compact timestamp, nothing else: at one per row the words
	/// cost more width than they carry. "Active since" survives for assistive tech only, so the
	/// timestamp is never announced as a bare number with no noun.
	/// </summary>
	public async Task The_row_pill_shows_a_stopwatch_and_a_timestamp_with_the_wording_kept_for_assistive_tech()
	{
		var workerId = await SeedEmployeeAsync("browse.compactpill", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Tile bathroom");
		var authCookie = await SignInAsync("browse.compactpill");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();

		body.Should().Contain("status-pill--compact");
		body.Should().Contain("#jt-icon-active");
		body.Should().Contain("<span class=\"visually-hidden\">Active since</span>");
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_time_from_the_browse_row()
	{
		var workerId = await SeedEmployeeAsync("browse.backdater", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Frame walls");
		var authCookie = await SignInAsync("browse.backdater");
		var backdated = DateTimeOffset.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, backdated);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_time_shows_a_helpful_error()
	{
		var workerId = await SeedEmployeeAsync("browse.future", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Dig trench");
		var authCookie = await SignInAsync("browse.future");
		var future = DateTimeOffset.UtcNow.AddHours(2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, future);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("in the future");
	}

	[Fact]
	public async Task Starting_a_session_with_a_malformed_backdate_from_the_browse_row_does_not_start_work()
	{
		var workerId = await SeedEmployeeAsync("browse.malformed", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Malformed browse start");
		var authCookie = await SignInAsync("browse.malformed");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, "not-a-local-date-time");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid date and time.");
		var sessions = await seedClient.Query.GetLeafSessionsAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, LeafWorkId = leaf.Id });
		sessions.Should().BeEmpty();
	}

	[Fact]
	public async Task A_worker_can_end_their_active_session_from_the_browse_row_via_the_work_page()
	{
		var workerId = await SeedEmployeeAsync("browse.finisher", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Lay bricks");
		var authCookie = await SignInAsync("browse.finisher");

		var (startCookie, startToken) = await GetBrowseFormAsync(authCookie);
		var startResponse = await PostStartAsync(authCookie, startCookie, startToken, leaf.Id, null);
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();

		// The always-present link to /Jobs/Work still works for finishing a session there too (ADR
		// 0046 adds an equivalent inline path on Browse itself; it doesn't remove this one).
		startBody.Should().Contain($"/Jobs/Work?leafNodeId={leaf.Id.Value}");

		var session = (await seedClient.Query.GetLeafSessionsAsync(
				new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, LeafWorkId = leaf.Id }))
			.Should().ContainSingle().Subject;

		var (workCookie, workToken) = await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		var finishResponse = await PostWorkFinishAsync(authCookie, workCookie, workToken, leaf.Id, session.Id.Value, session.Version, null);
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();
		finishBody.Should().Contain("Ends this session; the job stays In Progress.");
	}

	[Fact]
	public async Task A_worker_can_pick_up_an_unassigned_leaf_inline_from_the_browse_row()
	{
		_ = await SeedEmployeeAsync("browse.picker", EmployeeRole.Worker);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		var authCookie = await SignInAsync("browse.picker");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostPickUpAsync(authCookie, cookie, token, leaf.Id);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job node claimed");
	}

	[Fact]
	/// <summary>
	/// Submits the pick-up form exactly as the page renders it (every hidden field, names included)
	/// rather than a hand-built minimal body: the row form replays the page's own browsing state
	/// alongside the row's target, so a field name that collides with the page's bound
	/// <c>NodeId</c> makes the handler claim whatever node is being browsed instead of the row that
	/// was clicked. Posting only the target id hides that class of defect entirely.
	/// </summary>
	public async Task Picking_up_a_child_row_claims_that_child_rather_than_the_browsed_parent()
	{
		var pickerId = await SeedEmployeeAsync("browse.picker.row", EmployeeRole.Worker);
		var branch = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Owned branch",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var child = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = branch.Id,
			Description = "Unassigned child row",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		var authCookie = await SignInAsync("browse.picker.row");

		var browse = await GetAsync($"/Jobs/Browse?nodeId={branch.Id.Value}", authCookie);
		var (antiforgeryCookie, token) = await ExtractFormAsync(browse, string.Empty);
		var fields = ExtractHiddenFields(await browse.Content.ReadAsStringAsync(), "handler=PickUp", child.Id);
		fields["__RequestVerificationToken"] = token;

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=PickUp");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(fields);
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		(await reloaded.Content.ReadAsStringAsync()).Should().Contain("Job node claimed");

		var claimedChild = await seedClient.Query.GetJobNodeAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = child.Id,
		});
		claimedChild.Node.OwnerUserId.Should().Be(pickerId);

		var untouchedBranch = await seedClient.Query.GetJobNodeAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = branch.Id,
		});
		untouchedBranch.Node.OwnerUserId.Should().Be(administratorId);
	}

	[Fact]
	/// <summary>
	/// The claim control is drawn from the shared sprite like every other row action, and it lives on
	/// the record card's Owner field beside the "Unassigned" value — the field the action changes.
	/// Exactly one control per unassigned node: the toolbar does not carry a second copy for the node
	/// being browsed, which would make the reader resolve a choice between two identical actions
	/// before acting.
	/// </summary>
	public async Task An_unassigned_node_offers_the_pick_up_glyph_on_its_owner_field_and_its_rows()
	{
		_ = await SeedEmployeeAsync("browse.picker.glyph", EmployeeRole.Worker);
		var branch = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Unassigned branch",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = branch.Id,
			Description = "Unassigned grandchild",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		var authCookie = await SignInAsync("browse.picker.glyph");

		var body = await (await GetAsync($"/Jobs/Browse?nodeId={branch.Id.Value}", authCookie)).Content.ReadAsStringAsync();

		body.Should().Contain("#jt-icon-pick-up");
		// Always icon-only: the accessible name rides in a visually-hidden span and a title, never on
		// the glyph alone, and never as a labelled .btn.
		body.Should().Contain("class=\"jt-icon-button\" title=\"Pick up\"");
		body.Should().Contain("<span class=\"visually-hidden\">Pick up</span>");
		// The Owner field's own copy: the <dd> holding the owner value lays its value and claim button
		// out on one baseline.
		body.Should().Contain("class=\"w-75 mb-0 d-flex flex-wrap align-items-center gap-2\"");
		// One per unassigned subtree row, alongside the word it qualifies.
		body.Should().Contain("<span class=\"d-inline-flex align-items-center gap-2\">");
		// Two unassigned nodes on this page (the browsed branch and its one child), one control each,
		// contributing a title and a visually-hidden name apiece. A toolbar duplicate for the browsed
		// node would push this to six.
		(body.Split("Pick up").Length - 1).Should().Be(4);
	}

	[Fact]
	public async Task A_leaf_detail_toolbar_shows_finish_instead_of_start_once_a_session_is_active()
	{
		var workerId = await SeedEmployeeAsync("browse.toolbar", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Toolbar toggle leaf");
		var authCookie = await SignInAsync("browse.toolbar");

		var beforeResponse = await GetLeafDetailAsync(authCookie, leaf.Id);
		var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
		beforeBody.Should().Contain("#jt-icon-start");
		beforeBody.Should().NotContain("Active since");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		_ = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);

		var afterResponse = await GetLeafDetailAsync(authCookie, leaf.Id);
		var afterBody = await afterResponse.Content.ReadAsStringAsync();

		afterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		afterBody.Should().Contain("Active since");
		// Plan §4.1: the viewer's own one-click Start is replaced by Finish/pause, but the
		// authorized "Start for..." disclosure (also drawn with jt-icon-start) for another worker is
		// never removed -- only the viewer's own primary action toggles.
		afterBody.Should().NotContain("title=\"Start session\"");
	}

	[Fact]
	public async Task An_owner_can_start_a_session_for_another_worker_through_the_start_for_disclosure()
	{
		var ownerId = await SeedEmployeeAsync("browse.startfor.owner", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("browse.startfor.target", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Start-for leaf");
		var authCookie = await SignInAsync("browse.startfor.owner");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartForAsync(authCookie, cookie, token, leaf.Id, otherWorkerId, null);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started.");
	}

	[Fact]
	public async Task A_non_controlling_worker_cannot_start_a_session_for_another_worker()
	{
		var ownerId = await SeedEmployeeAsync("browse.startfor.bystander-owner", EmployeeRole.Worker);
		var bystanderId = await SeedEmployeeAsync("browse.startfor.bystander", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("browse.startfor.bystander-target", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Bystander start-for leaf");
		var authCookie = await SignInAsync("browse.startfor.bystander");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartForAsync(authCookie, cookie, token, leaf.Id, otherWorkerId, null);

		// Razor Pages' cookie-auth Forbid() redirects to the access-denied path rather than a raw 403
		// (matching this suite's existing convention for a page-handler denial, unlike the JSON API's
		// direct 403).
		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Found);
	}

	[Fact]
	public async Task The_start_for_disclosure_is_rendered_for_an_owner_but_not_for_a_non_controlling_worker()
	{
		var ownerId = await SeedEmployeeAsync("browse.startfor.render-owner", EmployeeRole.Worker);
		var bystanderId = await SeedEmployeeAsync("browse.startfor.render-bystander", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Render start-for leaf");

		var ownerCookie = await SignInAsync("browse.startfor.render-owner");
		var ownerResponse = await GetLeafDetailAsync(ownerCookie, leaf.Id);
		var ownerBody = await ownerResponse.Content.ReadAsStringAsync();
		ownerBody.Should().Contain("Start for…");

		var bystanderCookie = await SignInAsync("browse.startfor.render-bystander");
		var bystanderResponse = await GetLeafDetailAsync(bystanderCookie, leaf.Id);
		var bystanderBody = await bystanderResponse.Content.ReadAsStringAsync();
		bystanderBody.Should().NotContain("Start for…");
	}

	[Fact]
	public async Task The_start_for_disclosure_uses_a_native_control_that_works_without_JavaScript()
	{
		var ownerId = await SeedEmployeeAsync("browse.startfor.no-script-owner", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "No-script start-for leaf");
		var authCookie = await SignInAsync("browse.startfor.no-script-owner");

		var response = await GetLeafDetailAsync(authCookie, leaf.Id);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain($"<details class=\"jt-start-for-disclosure\" id=\"start-for-{leaf.Id.Value}\">");
		// The leaf-detail toolbar renders the labelled summary variant (a peer to Start session), not
		// the icon-only summary the dense per-row cell keeps -- either way still a native details/summary.
		body.Should().Contain("<summary class=\"btn btn-secondary jt-start-for-summary\">");
		body.Should().Contain("name=\"StartForUserId\"");
	}

	private async Task<HttpResponseMessage> PostStartForAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, AppUserId startForUserId, string? startedAt)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=StartFor");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["leafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["StartForUserId"] = startForUserId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (startedAt is not null) {
			fields["startedAt"] = startedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	[Fact]
	public async Task Three_simultaneous_active_workers_are_all_named_never_collapsed_to_one()
	{
		// Plan §2.4/§6 test matrix: a two-row fixture can accidentally pass code that treats one
		// session as "primary" -- this leaf has three concurrently active workers, none of them
		// collapsed away, and the viewer's own labelled "You" ahead of the others.
		var viewerId = await SeedEmployeeAsync("browse.threeactive.viewer", EmployeeRole.Worker);
		var aliceId = await SeedEmployeeAsync("browse.threeactive.alice", EmployeeRole.Worker);
		var bobId = await SeedEmployeeAsync("browse.threeactive.bob", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, viewerId, "Three active leaf");
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = viewerId,
		});
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = aliceId,
		});
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = bobId,
		});
		var authCookie = await SignInAsync("browse.threeactive.viewer");

		var response = await GetLeafDetailAsync(authCookie, leaf.Id);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("3 active");
		body.Should().Contain("You");
		body.Should().Contain("browse.threeactive.alice");
		body.Should().Contain("browse.threeactive.bob");
	}

	[Fact]
	public async Task Other_workers_sessions_are_never_finishable_inline_only_via_the_Sessions_page()
	{
		var ownerId = await SeedEmployeeAsync("browse.finish-target.owner", EmployeeRole.Worker);
		var aliceId = await SeedEmployeeAsync("browse.finish-target.alice", EmployeeRole.Worker);
		var bobId = await SeedEmployeeAsync("browse.finish-target.bob", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Exact finish targets");
		foreach (var workerId in new[] { aliceId, bobId }) {
			_ = await seedClient.Work.StartSessionAsync(new() {
				Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
				LeafWorkId = leaf.Id,
				WorkedByUserId = workerId,
			});
		}

		var authCookie = await SignInAsync("browse.finish-target.owner");

		var response = await GetLeafDetailAsync(authCookie, rootId);
		var body = await response.Content.ReadAsStringAsync();

		// With two or more workers the row never sprouts a finish button per worker (confusing, and
		// unbounded). The viewer is not working, so there is no inline finish at all here; every
		// worker's session is managed on the leaf's own Sessions page (the always-present link).
		body.Should().NotContain("Finish / pause");
		body.Should().NotContain("'s session\"");
		body.Should().Contain("title=\"Sessions\"");
		body.Should().Contain($"leafNodeId={leaf.Id.Value}");
	}

	[Fact]
	public async Task A_lone_other_session_stays_sessions_linked_for_a_permitted_manager()
	{
		var ownerId = await SeedEmployeeAsync("browse.lone.owner", EmployeeRole.Worker);
		var aliceId = await SeedEmployeeAsync("browse.lone.alice", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Lone other session leaf");
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = aliceId,
		});
		var authCookie = await SignInAsync("browse.lone.owner");

		var response = await GetLeafDetailAsync(authCookie, rootId);
		var body = await response.Content.ReadAsStringAsync();

		// Exactly one active session, owned by someone else: the leaf owner (a permitted manager) still
		// reaches the pause/complete decision through the always-present "Sessions" link (plan §5.3),
		// which resolves the same lone session unambiguously.
		body.Should().Contain("title=\"Sessions\"");
	}

	[Fact]
	public async Task A_lone_other_session_is_not_finishable_inline_without_manage_permission()
	{
		var ownerId = await SeedEmployeeAsync("browse.lone-noperm.owner", EmployeeRole.Worker);
		var aliceId = await SeedEmployeeAsync("browse.lone-noperm.alice", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("browse.lone-noperm.bystander", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Lone other no-permission leaf");
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = aliceId,
		});
		var authCookie = await SignInAsync("browse.lone-noperm.bystander");

		var response = await GetLeafDetailAsync(authCookie, rootId);
		var body = await response.Content.ReadAsStringAsync();

		// A worker who neither owns nor may manage the leaf sees no inline finish for another worker's
		// lone session -- "if the person has appropriate permissions" gates the inline control.
		body.Should().NotContain("Finish / pause");
	}

	[Fact]
	public async Task With_several_active_workers_a_browse_row_still_finishes_only_the_viewers_own_session()
	{
		var viewerId = await SeedEmployeeAsync("browse.myfinish.viewer", EmployeeRole.Worker);
		var aliceId = await SeedEmployeeAsync("browse.myfinish.alice", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, viewerId, "Mixed active leaf");
		foreach (var workerId in new[] { viewerId, aliceId }) {
			_ = await seedClient.Work.StartSessionAsync(new() {
				Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
				LeafWorkId = leaf.Id,
				WorkedByUserId = workerId,
			});
		}

		var authCookie = await SignInAsync("browse.myfinish.viewer");

		var response = await GetLeafDetailAsync(authCookie, rootId);
		var body = await response.Content.ReadAsStringAsync();

		// No inline finish for anyone (plan §5.3): the viewer's own session and every other worker's
		// session are both reached through the same always-present "Sessions" link.
		body.Should().NotContain("Finish / pause");
		body.Should().NotContain("'s session\"");
		body.Should().Contain("title=\"Sessions\"");
	}

	[Fact]
	public async Task Two_active_workers_show_a_count_pill_naming_both()
	{
		var viewerId = await SeedEmployeeAsync("browse.twoactive.viewer", EmployeeRole.Worker);
		var aliceId = await SeedEmployeeAsync("browse.twoactive.alice", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, viewerId, "Two active leaf");
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = viewerId,
		});
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = aliceId,
		});
		var authCookie = await SignInAsync("browse.twoactive.viewer");

		var response = await GetLeafDetailAsync(authCookie, leaf.Id);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("2 active");
		body.Should().NotContain("Active since", "the singular pill wording is reserved for exactly one active worker");
	}

	[Fact]
	public async Task A_single_active_worker_who_is_not_the_viewer_is_named_in_the_singular_pill()
	{
		var viewerId = await SeedEmployeeAsync("browse.singleother.viewer", EmployeeRole.Worker);
		var aliceId = await SeedEmployeeAsync("browse.singleother.alice", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, viewerId, "Single other active leaf");
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = aliceId,
		});
		var authCookie = await SignInAsync("browse.singleother.viewer");

		var response = await GetLeafDetailAsync(authCookie, leaf.Id);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("Active since");
		body.Should().Contain("browse.singleother.alice");
	}

	[Fact]
	public async Task A_browse_row_offers_start_as_an_icon_beside_the_backdate_disclosure()
	{
		var workerId = await SeedEmployeeAsync("browse.icons", EmployeeRole.Worker);
		_ = await AddWorkedLeafAsync(rootId, workerId, "Icon row leaf");
		var authCookie = await SignInAsync("browse.icons");

		var response = await GetLeafDetailAsync(authCookie, rootId);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("#jt-icon-start");
		body.Should().Contain("#jt-icon-backdate");
		body.Should().NotContain(">Start</button>");
	}

	[Fact]
	public async Task A_browse_row_shows_only_the_sessions_icon_once_a_session_is_active()
	{
		var workerId = await SeedEmployeeAsync("browse.finish-icon", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Finish icon row leaf");
		var authCookie = await SignInAsync("browse.finish-icon");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		_ = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);

		var response = await GetLeafDetailAsync(authCookie, rootId);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("#jt-icon-sessions");
		body.Should().NotContain("btn btn-secondary\">Finish / pause");
	}

	[Fact]
	public async Task The_leaf_detail_view_embeds_its_own_sessions_panel_alongside_the_link_to_the_unified_work_page()
	{
		var workerId = await SeedEmployeeAsync("browse.finish-label", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Labelled finish leaf");
		var authCookie = await SignInAsync("browse.finish-label");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		_ = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);

		var response = await GetLeafDetailAsync(authCookie, leaf.Id);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		// ADR 0046: the link to /Jobs/Work survives (it is where the achievement-changing actions
		// live), but the leaf's own Sessions history/Finish panel is now embedded here too, not only
		// reachable through that link. Browse always shows every worker's sessions -- the worker-filter
		// form is a Work-page-only follow-up narrowing tool, not repeated here.
		body.Should().Contain($"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		body.Should().NotContain(">Start session<");
		body.Should().NotContain("Filter by worker");
		body.Should().Contain("<h2>Sessions</h2>");
	}

	[Fact]
	public async Task A_worker_can_pause_their_own_session_directly_from_the_browse_leaf_detail_panel()
	{
		var workerId = await SeedEmployeeAsync("browse.inline-finisher", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Inline finish leaf");
		var authCookie = await SignInAsync("browse.inline-finisher");

		var (startCookie, startToken) = await GetBrowseFormAsync(authCookie);
		_ = await PostStartAsync(authCookie, startCookie, startToken, leaf.Id, null);

		var session = (await seedClient.Query.GetLeafSessionsAsync(
				new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, LeafWorkId = leaf.Id }))
			.Should().ContainSingle().Subject;

		var (cookie, token) = await GetFormAsync(authCookie, $"/Jobs/Browse?nodeId={leaf.Id.Value}");
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=Finish");
		request.Headers.Add("Cookie", $"{authCookie}; {cookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["NodeId"] = leaf.Id.Value.ToString(CultureInfo.InvariantCulture),
			["sessionId"] = session.Id.Value.ToString(CultureInfo.InvariantCulture),
			["version"] = session.Version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Ends this session; the job stays In Progress.");
	}

	/// <summary>
	///     "In Progress with nobody clocked on" is a paused leaf, not an invalid one (ADR 0045: zero
	///     active sessions is valid from <c>InProgress</c>) — it is what Pause job produces every time,
	///     so Browse names it rather than leaving the Active column indistinguishable from a leaf
	///     nobody has ever started.
	/// </summary>
	[Fact]
	public async Task A_paused_leaf_reads_as_paused_on_browse()
	{
		var workerId = await SeedEmployeeAsync("browse.paused", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Paused browse leaf");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			SessionId = session.Id,
			Version = session.Version,
		});
		var authCookie = await SignInAsync("browse.paused");

		var leafView = await GetAsync($"/Jobs/Browse?nodeId={leaf.Id.Value}", authCookie);
		var leafBody = await leafView.Content.ReadAsStringAsync();
		leafBody.Should().Contain("status-pill-paused", "the leaf's own detail view names the paused state");

		var parentView = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var parentBody = await parentView.Content.ReadAsStringAsync();
		parentBody.Should().Contain("status-pill-paused", "the row in the subtree table names it too");
	}

	[Fact]
	public async Task A_leaf_nobody_has_started_is_not_marked_paused_on_browse()
	{
		var workerId = await SeedEmployeeAsync("browse.not-paused", EmployeeRole.Worker);
		_ = await AddWorkedLeafAsync(rootId, workerId, "Never started leaf");
		var authCookie = await SignInAsync("browse.not-paused");

		var response = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain("status-pill-paused", "Waiting with no session is idle, not paused");
	}

	[Fact]
	public async Task Work_page_exposes_a_worked_by_employee_selector()
	{
		var ownerId = await SeedEmployeeAsync("work.selector.owner", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("work.selector.other", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Selector leaf");
		var authCookie = await SignInAsync("work.selector.owner");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("<select");
		body.Should().Contain("name=\"WorkedByUserId\"");
		body.Should().Contain($"value=\"{otherWorkerId.Value}\">work.selector.other");
	}

	[Fact]
	public async Task A_terminal_leaf_marks_its_closure_with_a_pill_without_rendering_start_controls()
	{
		var workerId = await SeedEmployeeAsync("browse.closed-terminal", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Completed leaf");
		await SetAchievementAsync(leaf.Id, Achievement.Success);
		var authCookie = await SignInAsync("browse.closed-terminal");

		var browseResponse = await GetLeafDetailAsync(authCookie, leaf.Id);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();
		var workResponse = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var workBody = await workResponse.Content.ReadAsStringAsync();

		foreach (var body in new[] { browseBody, workBody }) {
			// The Active state is a concise "Closed" pill, never the verbose closure sentence -- and
			// never in the Actions cell, which is buttons only.
			body.Should().Contain("status-pill-closed");
			body.Should().NotContain("Reopen it before starting another session");
			body.Should().NotContain(">Start session</button>");
			body.Should().NotContain("Start for…");
			body.Should().Contain("Sessions");
		}
	}

	[Fact]
	public async Task An_archived_leaf_marks_its_closure_with_a_pill_without_rendering_start_controls()
	{
		var workerId = await SeedEmployeeAsync("browse.closed-archived", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Archived leaf");
		var current = await seedClient.Query.GetJobNodeAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = leaf.Id,
		});
		_ = await seedClient.Jobs.ArchiveAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = leaf.Id,
			Version = current.Node.Version,
		});
		var authCookie = await SignInAsync("browse.closed-archived");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("status-pill-closed");
		body.Should().NotContain("Restore it before starting another session");
		body.Should().NotContain(">Start session</button>");
		body.Should().NotContain("Start for…");
		body.Should().Contain("Sessions");
	}

	[Fact]
	public async Task A_browse_session_mutation_preserves_the_unassigned_filter_through_prg()
	{
		var workerId = await SeedEmployeeAsync("browse.preserve-filters", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Preserve Browse state");
		var authCookie = await SignInAsync("browse.preserve-filters");
		var initial = await GetAsync($"/Jobs/Browse?nodeId={leaf.Id.Value}&unassignedOnly=true", authCookie);
		var initialBody = await initial.Content.ReadAsStringAsync();
		var (antiforgeryCookie, token) = await ExtractFormAsync(initial, string.Empty);

		initialBody.Should().Contain("name=\"UnassignedOnly\" value=\"True\"");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=Start");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["leafNodeId"] = leaf.Id.Value.ToString(CultureInfo.InvariantCulture),
			["NodeId"] = leaf.Id.Value.ToString(CultureInfo.InvariantCulture),
			["UnassignedOnly"] = bool.TrueString,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("unassignedOnly=True");
	}

	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	private async Task<HttpResponseMessage> FollowRedirectAsync(HttpResponseMessage response, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
		var cookieHeader = string.Join("; ", new[] { authCookie }.Concat(ExtractSetCookiePairs(response)));
		request.Headers.Add("Cookie", cookieHeader);

		return await client.SendAsync(request);
	}

	private static IEnumerable<string> ExtractSetCookiePairs(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(ExtractCookiePair) : [];

	private async Task<HttpResponseMessage> GetLeafDetailAsync(string authCookie, JobNodeId nodeId) =>
		await GetAsync($"/Jobs/Browse?nodeId={nodeId.Value}", authCookie);

	private async Task<HttpResponseMessage> GetAsync(string requestUri, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostPickUpAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=PickUp");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			[PickUpButtonModel.NodeFieldName] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<JobNodeResult> AddWorkedLeafAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});

		return leaf;
	}

	private async Task SetAchievementAsync(JobNodeId leafId, Achievement achievement)
	{
		var leafWork = await seedClient.Query.GetLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
		});
		var inProgress = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Seed terminal leaf",
			Version = leafWork.Version,
		});
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			NewAchievement = achievement,
			Reason = "Seed terminal leaf",
			Version = inProgress.Version,
		});
	}

	private async Task<HttpResponseMessage> PostStartAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, string? startedAt)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=Start");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["leafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (startedAt is not null) {
			fields["startedAt"] = startedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	/// <summary>Finishes a session via <c>/Jobs/Work</c>'s own Finish handler (also reachable inline from Browse itself, ADR 0046).</summary>
	private async Task<HttpResponseMessage> PostWorkFinishAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, long sessionId, long version, string? finishedAt)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Finish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["leafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["sessionId"] = sessionId.ToString(CultureInfo.InvariantCulture),
			["version"] = version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (finishedAt is not null) {
			fields["finishedAt"] = finishedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException($"No antiforgery token in {path} body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<(string CookieHeader, string Token)> GetBrowseFormAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/Browse");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Browse response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Browse body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static async Task<(string CookieHeader, string Token)> ExtractFormAsync(HttpResponseMessage response, string previousAntiforgeryCookie)
	{
		var body = await response.Content.ReadAsStringAsync();
		var newCookie = FindSetCookie(response, "Antiforgery");
		var cookie = newCookie is not null ? ExtractCookiePair(newCookie) : previousAntiforgeryCookie;
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in response body.");

		return (cookie, token);
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

	/// <summary>
	///     The hidden fields of the one rendered <c>&lt;form&gt;</c> that posts to
	///     <paramref name="actionContains" /> and carries <paramref name="targetNodeId" /> as a field
	///     value — i.e. the form belonging to that node's own row, whatever its fields happen to be
	///     named. Matching on the value rather than a field name is deliberate: it lets a test submit
	///     the real form without restating the markup's field names, so a rename cannot quietly turn a
	///     test that exercised the page into one that only exercises the handler signature.
	/// </summary>
	private static Dictionary<string, string> ExtractHiddenFields(string body, string actionContains, JobNodeId targetNodeId)
	{
		var targetValue = targetNodeId.Value.ToString(CultureInfo.InvariantCulture);
		var form = body.Split("<form", StringSplitOptions.RemoveEmptyEntries)
					   .Where(segment => segment.Contains(actionContains, StringComparison.Ordinal))
					   .Select(segment => segment[..segment.IndexOf("</form>", StringComparison.Ordinal)])
					   .SingleOrDefault(segment => HiddenFieldPattern().Matches(segment)
						   .Any(field => field.Groups["value"].Value == targetValue))
				   ?? throw new InvalidOperationException(
					   $"No '{actionContains}' form carrying node {targetValue} in the rendered body.");

		return HiddenFieldPattern().Matches(form)
			.ToDictionary(field => field.Groups["name"].Value, field => field.Groups["value"].Value);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("<input type=\"hidden\" name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]*)\"")]
	private static partial Regex HiddenFieldPattern();


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
