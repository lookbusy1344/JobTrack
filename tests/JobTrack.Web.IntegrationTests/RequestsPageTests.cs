namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
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
///     Direct-HTTP tests for the requester self-service page (ADR 0033, plan §8 <c>/Requests</c>):
///     submitting a request into an eligible holding area, seeing only the requester's own submitted
///     requests, and confirming the Requester role cannot reach the operational job tree.
/// </summary>
public sealed partial class RequestsPageTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const short PriorityMedium = 2;

	/// <summary>
	///     The row-title truncation budget, restated here rather than read from <c>JobNodeDisplay</c>
	///     (internal to <c>JobTrack.Web</c>): a test that read the production constant would assert the
	///     behaviour against itself and pass at any value.
	/// </summary>
	private const int RowTitleMaxDescriptionLength = 100;

	private const string LongDescription =
		"The third-floor colour printer jams on every duplex job and the front panel shows a paper-path "
		+ "error that clears itself before anyone can read the code";

	/// <summary>
	///     The truncated row title as it appears in the response body — Razor's encoder emits the
	///     non-ASCII ellipsis as a numeric character reference, so the raw HTML never contains a
	///     literal "…" to match against.
	/// </summary>
	private static readonly string TruncatedDescription =
		HtmlEncoder.Default.Encode(LongDescription[..RowTitleMaxDescriptionLength] + "…");

	/// <summary>
	///     A description built so the cut lands exactly on a word gap: the first
	///     <see cref="RowTitleMaxDescriptionLength" /> - 1 characters are non-space, and the character at
	///     that index is the space, so a naive slice would leave a dead space before the ellipsis.
	/// </summary>
	private static readonly string BoundarySpaceDescription =
		"Scanner fault".PadRight(RowTitleMaxDescriptionLength - 1, '.') + " trailing words beyond the cut";

	/// <summary>The expected row title for <see cref="BoundarySpaceDescription" />: the boundary space dropped.</summary>
	private static readonly string BoundarySpaceTruncated =
		HtmlEncoder.Default.Encode(BoundarySpaceDescription[..(RowTitleMaxDescriptionLength - 1)] + "…");

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
			UserName = "admin.requests-tests",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrapResult.AdministratorId;
		rootId = bootstrapResult.RootJobNodeId;

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
	public async Task A_requester_can_submit_a_request_and_see_it_in_their_own_list()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.requester", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.requester");

		var (antiforgeryCookie, token) = await GetPageFormAsync(authCookie);
		var response = await PostSubmitAsync(authCookie, antiforgeryCookie, token, holdingAreaId, "Printer will not turn on");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Printer will not turn on");
	}

	[Fact]
	public async Task A_description_containing_script_markup_is_rendered_html_encoded_not_as_live_markup()
	{
		const string InjectedDescription = "<script>alert('xss')</script>";
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.xss", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.xss");

		var (antiforgeryCookie, token) = await GetPageFormAsync(authCookie);
		var response = await PostSubmitAsync(authCookie, antiforgeryCookie, token, holdingAreaId, InjectedDescription);
		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();

		body.Should().NotContain(InjectedDescription);
		body.Should().Contain("&lt;script&gt;alert(&#x27;xss&#x27;)&lt;/script&gt;");
	}

	[Fact]
	public async Task Submitting_a_blank_description_shows_a_validation_error_and_does_not_submit()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.blank", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.blank");

		var (antiforgeryCookie, token) = await GetPageFormAsync(authCookie);
		var response = await PostSubmitAsync(authCookie, antiforgeryCookie, token, holdingAreaId, string.Empty);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().NotContain("submitted.");
	}

	[Fact]
	public async Task A_requester_does_not_see_another_requesters_submitted_request()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.owner", EmployeeRole.Requester);
		var ownerCookie = await SignInAsync("rita.owner");
		var (ownerAntiforgery, ownerToken) = await GetPageFormAsync(ownerCookie);
		_ = await PostSubmitAsync(ownerCookie, ownerAntiforgery, ownerToken, holdingAreaId, "Owner's private request");

		_ = await SeedEmployeeAsync("ravi.other", EmployeeRole.Requester);
		var otherCookie = await SignInAsync("ravi.other");

		var otherPage = await GetPageAsync(otherCookie);
		var otherBody = await otherPage.Content.ReadAsStringAsync();

		otherBody.Should().NotContain("Owner's private request");
	}

	[Fact]
	public async Task A_requester_cannot_reach_the_operational_job_browse_page()
	{
		_ = await SeedEmployeeAsync("rita.blocked", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.blocked");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={rootId.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);

		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Found);
	}

	[Fact]
	public async Task Landing_redirects_a_requester_to_their_requests()
	{
		_ = await SeedEmployeeAsync("rita.landing", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.landing");

		using var request = new HttpRequestMessage(HttpMethod.Get, "/");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be("/Requests");
	}

	[Fact]
	public async Task A_worker_cannot_reach_the_requests_page()
	{
		_ = await SeedEmployeeAsync("wanda.worker", EmployeeRole.Worker);
		var authCookie = await SignInAsync("wanda.worker");

		var response = await GetPageAsync(authCookie);

		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Found);
	}

	[Fact]
	public async Task A_requester_can_view_their_own_request_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.detail", EmployeeRole.Requester, "Rita Detail");
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		var authCookie = await SignInAsync("rita.detail");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("<h1>Job request</h1>", "the head names the kind of page above the job, as /Jobs/Work does");
		body.Should().Contain("Printer will not turn on");
		body.Should().NotContain("<h2 class=\"jt-preserve-whitespace\"><a ",
			"this page is the request's own home, so its title is not a link back to itself");
		body.Should().Contain("<dt class=\"w-25 text-nowrap\">Requester</dt>");
		body.Should().Contain("<span class=\"jt-tag\">Rita Detail (rita.detail)</span>",
			"the requester reads as one 'display name (username)' tag, as an owner does in Browse");
		body.Should().NotContain(">Username<", "the separate username field is folded into the requester tag");
		body.Should().Contain("<a href=\"/Requests\">&larr; Back</a>");
	}

	/// <summary>
	///     The record card is Browse's own (ADR 0044): the two-value subtree rollup and the readiness
	///     pill, not the requester-status vocabulary — a request's anchor may be a leaf or, after triage
	///     decomposes it, a branch, and only those two forms are defined for both.
	/// </summary>
	[Fact]
	public async Task Request_detail_shows_the_subtree_achievement_and_readiness_in_browses_own_style()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.rollup", EmployeeRole.Requester, "Rita Rollup");
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Rollup and readiness fields");
		var authCookie = await SignInAsync("rita.rollup");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("<dt class=\"w-25 text-nowrap\">Achievement</dt>");
		body.Should().Contain("href=\"#jt-icon-achievement-waiting\"",
			"an anchor with no succeeded leaf beneath it rolls up to Unfinished, which borrows the waiting glyph");
		body.Should().Contain("<dt class=\"w-25 text-nowrap\">Readiness</dt>");
		body.Should().Contain("No blocks");
		body.Should().NotContain("<dt class=\"w-25 text-nowrap\">Status</dt>",
			"an anchor with no leaf work yet has no finer achievement to show");
	}

	/// <summary>
	///     Only a leaf carries the six-value achievement vocabulary the rollup collapses away, so the
	///     extra Status field appears exactly when there is a finer state to name.
	/// </summary>
	[Fact]
	public async Task Request_detail_adds_the_leafs_own_achievement_when_the_anchor_is_a_worked_leaf()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.leafstatus", EmployeeRole.Requester, "Rita Leafstatus");
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Worked anchor leaf");
		var context = new CommandContext { Actor = administratorId, CorrelationId = Guid.NewGuid() };
		var leafWork = await seedClient.Jobs.AttachLeafWorkAsync(new() { Context = context, JobNodeId = submitted.JobNodeId });
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = submitted.JobNodeId,
			NewAchievement = Achievement.InProgress,
			Reason = "Exercise the leaf status field",
			Version = leafWork.Version,
		});
		var authCookie = await SignInAsync("rita.leafstatus");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("<dt class=\"w-25 text-nowrap\">Status</dt>");
		body.Should().Contain("href=\"#jt-icon-achievement-in-progress\"");
	}

	/// <summary>
	///     Readiness aggregates prerequisites declared on the anchor and on every ancestor (spec §6) —
	///     facts outside the requester-safe subtree, composed by <c>RequestCommands</c> rather than by the
	///     request port. Blocked is a state, not an error (ADR 0051): the page names it, it does not fail.
	/// </summary>
	[Fact]
	public async Task Request_detail_shows_a_blocked_pill_when_a_prerequisite_is_unsatisfied()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.blocked", EmployeeRole.Requester, "Rita Blocked");
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Blocked by a prerequisite");
		var context = new CommandContext { Actor = administratorId, CorrelationId = Guid.NewGuid() };
		var blocker = await seedClient.Jobs.AddChildAsync(new() {
			Context = context,
			ParentId = rootId,
			Description = "Must finish first",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() { Context = context, JobNodeId = blocker.Id });
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = context,
			DependentJobId = submitted.JobNodeId,
			RequiredJobId = blocker.Id,
		});
		var authCookie = await SignInAsync("rita.blocked");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("<dt class=\"w-25 text-nowrap\">Readiness</dt>");
		body.Should().Contain("status-pill-blocked");
		body.Should().Contain(">Blocked</span>");
		body.Should().NotContain("No blocks");
	}

	[Fact]
	public async Task Request_detail_draws_a_decomposed_subtree_and_shows_each_nodes_time_without_cost()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.decomposed", EmployeeRole.Requester);
		var workerId = await SeedEmployeeAsync("wanda.decomposed", EmployeeRole.Worker);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Repair the print room printer");
		var context = new CommandContext { Actor = administratorId, CorrelationId = Guid.NewGuid() };
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() { Context = context, JobNodeId = submitted.JobNodeId });
		var decomposition = await seedClient.Jobs.DecomposeWorkedLeafAsync(new() {
			Context = context,
			LeafNodeId = submitted.JobNodeId,
			Version = submitted.Version,
			BranchDescription = "Repair the print room printer",
			ExistingWorkDescription = "Diagnose paper feed",
			NewChildren = [
				new() { Description = "Replace feed roller", OwnerUserId = workerId, Priority = Priority.Medium },
			],
		});
		var replacementId = decomposition.NewChildIds.Single();
		var replacementWork = await seedClient.Jobs.AttachLeafWorkAsync(new() { Context = context, JobNodeId = replacementId });
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = replacementId,
			NewAchievement = Achievement.InProgress,
			Reason = "Exercise requester progress icon",
			Version = replacementWork.Version,
		});
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = context,
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 18, 0)),
				null),
			Reason = "Working window for requester duration test",
		});
		await AddFinishedSessionAsync(
			workerId, decomposition.ExistingWorkChildId,
			Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 11, 0));
		await AddFinishedSessionAsync(
			workerId, replacementId,
			Instant.FromUtc(2026, 1, 1, 12, 0), Instant.FromUtc(2026, 1, 1, 15, 0));
		var authCookie = await SignInAsync("rita.decomposed");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("<th scope=\"col\" class=\"text-end\">Time worked</th>");
		body.Should().NotContain("<th scope=\"col\">Status</th>");
		body.Should().Contain("href=\"#jt-icon-branch\"");
		body.Should().Contain("href=\"#jt-icon-leaf\"");
		body.Should().Contain("Diagnose paper feed");
		body.Should().Contain("href=\"#jt-icon-achievement-in-progress\"");
		MyRegex().IsMatch(body)
			.Should().BeTrue("the public status icon should immediately follow the leaf name, as it does in Browse");
		body.Should().Contain(">5.0 hrs<");
		body.Should().Contain(">2.0 hrs<");
		body.Should().Contain(">3.0 hrs<");
		body.Should().NotContain(">&#xA3;");
		body.Should().NotContain(">Cost<");
		body.Should().NotContain(">Sessions<");
	}

	[Fact]
	public async Task A_different_requester_cannot_view_someone_elses_request_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.detail-owner", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Owner's private request");
		_ = await SeedEmployeeAsync("ravi.detail-stranger", EmployeeRole.Requester);
		var strangerCookie = await SignInAsync("ravi.detail-stranger");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, strangerCookie);

		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Found);
	}

	[Fact]
	public async Task A_requester_can_add_a_note_from_the_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.note", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		var authCookie = await SignInAsync("rita.note");
		const string ReturnUrl = "/Requests?view=recent";

		var (antiforgeryCookie, token) =
			await GetDetailPageFormAsync(submitted.JobNodeId.Value, authCookie, ReturnUrl);
		var response = await PostAddNoteAsync(
			submitted.JobNodeId.Value,
			authCookie,
			antiforgeryCookie,
			token,
			"Any update?",
			ReturnUrl);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Any update?");
		body.Should().Contain("<a href=\"/Requests?view=recent\">&larr; Back</a>");
	}

	[Fact]
	public async Task Staff_see_public_and_private_note_visibility_in_a_compact_footer()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.note-visibility", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		var jobManagerId = await SeedEmployeeAsync("priya.note-visibility", EmployeeRole.JobManager);
		var context = new CommandContext { Actor = jobManagerId, CorrelationId = Guid.NewGuid() };
		_ = await seedClient.Requests.AddNoteAsync(new() {
			Context = context,
			NodeId = submitted.JobNodeId,
			Content = "Visible progress update",
			VisibleToRequester = true,
		});
		_ = await seedClient.Requests.AddNoteAsync(new() {
			Context = context with { CorrelationId = Guid.NewGuid() },
			NodeId = submitted.JobNodeId,
			Content = "Internal triage note",
			VisibleToRequester = false,
		});
		var staffCookie = await SignInAsync("priya.note-visibility");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, staffCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Visible progress update");
		body.Should().Contain("Internal triage note");
		body.Should().Contain("<p class=\"text-muted mb-0 d-flex flex-wrap align-items-center gap-2\">");
		body.Should().Contain("<span class=\"status-pill status-pill-ready status-pill--compact\">Public</span>");
		body.Should().Contain("<span class=\"status-pill status-pill-closed status-pill--compact\">Private</span>");
		body.Should().Contain("<div class=\"form-group mb-2\">");
	}

	[Fact]
	public async Task Empty_note_validation_message_does_not_reserve_vertical_space()
	{
		var response = await client.GetAsync("/css/site.css");
		var css = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		css.Should().Contain(".field-validation-valid {\n    display: none;\n}");
	}

	[Fact]
	public async Task A_job_manager_can_acknowledge_a_request_from_the_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.for-ack", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		_ = await SeedEmployeeAsync("priya.jobmanager", EmployeeRole.JobManager);
		var staffCookie = await SignInAsync("priya.jobmanager");

		var (antiforgeryCookie, token) = await GetDetailPageFormAsync(submitted.JobNodeId.Value, staffCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Requests/{submitted.JobNodeId.Value}?handler=Acknowledge");
		request.Headers.Add("Cookie", $"{staffCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["version"] = submitted.Version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, staffCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Acknowledged");
		body.Should().Contain($"<a href=\"/Jobs/Browse?nodeId={submitted.JobNodeId.Value}\">&larr; Back</a>");
	}

	[Fact]
	public async Task A_job_manager_browsing_a_submitted_request_sees_a_request_action_without_duplicate_status()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.triage", EmployeeRole.Requester, "Client Requester");
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		_ = await SeedEmployeeAsync("priya.triage-manager", EmployeeRole.JobManager);
		var staffCookie = await SignInAsync("priya.triage-manager");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={submitted.JobNodeId.Value}");
		request.Headers.Add("Cookie", staffCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("Requester request");
		body.Should().NotContain(">Submitted</span>");
		body.Should().Contain("<dt class=\"w-25 text-nowrap\">Requester</dt>");
		body.Should().Contain("<a class=\"jt-tag\" href=\"/Requests/");
		body.Should().Contain(">Client Requester (rita.triage)</a>");
		body.IndexOf(">Priority</dt>", StringComparison.Ordinal).Should()
			.BeLessThan(body.IndexOf(">Requester</dt>", StringComparison.Ordinal),
				"the two-column card should place Priority below Kind and Requester below Owner");
		body.Should().Contain($"href=\"/Requests/{submitted.JobNodeId.Value}?returnUrl=");
		body.Should().Contain(
			$"<a class=\"btn btn-secondary\" href=\"/Requests/{submitted.JobNodeId.Value}?returnUrl=%2FJobs%2FBrowse");
		body.Should().Contain(">Request</a>");
	}

	[Fact]
	public async Task A_job_manager_browsing_an_ordinary_node_sees_no_request_action()
	{
		_ = await SeedEmployeeAsync("priya.no-request", EmployeeRole.JobManager);
		var staffCookie = await SignInAsync("priya.no-request");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={rootId.Value}");
		request.Headers.Add("Cookie", staffCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("Requester request");
		body.Should().NotContain(">Requester</dt>");
		body.Should().NotContain("/Requests/");
		body.Should().NotMatchRegex(">\\s*Request\\s*</a>");
	}

	[Fact]
	public async Task A_job_worker_can_return_to_a_local_staff_page_but_not_an_external_url()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.return-target", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		var workerId = await SeedEmployeeAsync("will.return-target", EmployeeRole.Worker);
		_ = await seedClient.Jobs.PickUpAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			NodeId = submitted.JobNodeId,
		});
		var staffCookie = await SignInAsync("will.return-target");
		const string LocalReturnUrl = "/Jobs/AwaitingProgress?showWholeTree=true";

		var localResponse = await GetDetailPageAsync(submitted.JobNodeId.Value, staffCookie, LocalReturnUrl);
		var localBody = await localResponse.Content.ReadAsStringAsync();
		localBody.Should().Contain("<a href=\"/Jobs/AwaitingProgress?showWholeTree=true\">&larr; Back</a>");

		var externalResponse = await GetDetailPageAsync(submitted.JobNodeId.Value, staffCookie, "https://example.test/escape");
		var externalBody = await externalResponse.Content.ReadAsStringAsync();
		externalBody.Should().Contain($"<a href=\"/Jobs/Browse?nodeId={submitted.JobNodeId.Value}\">&larr; Back</a>");
		externalBody.Should().NotContain("example.test");
	}

	[Fact]
	public async Task A_long_request_description_is_truncated_in_the_list_row_but_shown_in_full_on_the_detail_heading()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.verbose", EmployeeRole.Requester);
		// Seeded through the client rather than the HTTP form deliberately: a form submit leaves the
		// untruncated description in the page's success banner, which would mask the row truncation.
		var submitted = await SubmitAsync(requesterId, holdingAreaId, LongDescription);
		var authCookie = await SignInAsync("rita.verbose");

		var listBody = await (await GetPageAsync(authCookie)).Content.ReadAsStringAsync();
		listBody.Should().Contain(TruncatedDescription);
		listBody.Should().NotContain(LongDescription);

		var detailBody = await (await GetDetailPageAsync(submitted.JobNodeId.Value, authCookie)).Content.ReadAsStringAsync();
		detailBody.Should().Contain(LongDescription, "the detail page heading is where the full text lives");
		detailBody.Should().Contain(TruncatedDescription, "the subtree table row still truncates");
	}

	[Fact]
	public async Task A_row_title_cut_on_a_word_gap_drops_the_trailing_space_before_the_ellipsis()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.boundary", EmployeeRole.Requester);
		_ = await SubmitAsync(requesterId, holdingAreaId, BoundarySpaceDescription);
		var authCookie = await SignInAsync("rita.boundary");

		var listBody = await (await GetPageAsync(authCookie)).Content.ReadAsStringAsync();

		listBody.Should().Contain(BoundarySpaceTruncated);
		listBody.Should().NotContain(HtmlEncoder.Default.Encode(" …"), "a dead space before the ellipsis reads as a rendering fault");
	}

	private async Task<JobRequestResult> SubmitAsync(AppUserId requesterId, RequestHoldingAreaId holdingAreaId, string description) =>
		await seedClient.Requests.SubmitAsync(new() {
			Context = new() { Actor = requesterId, CorrelationId = Guid.NewGuid() },
			HoldingAreaId = holdingAreaId,
			Description = description,
		});

	private async Task AddFinishedSessionAsync(
		AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.CorrectSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			StartedAt = startedAt,
			FinishedAt = finishedAt,
			Reason = "Pin to a deterministic instant for requester duration test",
			Version = started.Version,
		});
	}

	private async Task<HttpResponseMessage> GetDetailPageAsync(long jobNodeId, string authCookie, string? returnUrl = null)
	{
		var path = returnUrl is null
			? $"/Requests/{jobNodeId}"
			: $"/Requests/{jobNodeId}?returnUrl={Uri.EscapeDataString(returnUrl)}";
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetDetailPageFormAsync(
		long jobNodeId,
		string authCookie,
		string? returnUrl = null)
	{
		var response = await GetDetailPageAsync(jobNodeId, authCookie, returnUrl);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie in request detail page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in request detail page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<HttpResponseMessage> PostAddNoteAsync(
		long jobNodeId,
		string authCookie,
		string antiforgeryCookie,
		string token,
		string content,
		string? returnUrl = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Requests/{jobNodeId}?handler=AddNote");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["NoteInput.Content"] = content,
			["returnUrl"] = returnUrl ?? string.Empty,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostSubmitAsync(
		string authCookie, string antiforgeryCookie, string token, RequestHoldingAreaId holdingAreaId, string description)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Requests?handler=Submit");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Submit.Description"] = description,
			["Submit.HoldingAreaId"] = holdingAreaId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
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

	private async Task<HttpResponseMessage> GetPageAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Requests");
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetPageFormAsync(string authCookie)
	{
		var response = await GetPageAsync(authCookie);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie in Requests page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Requests page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
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
		var authCookie = FindSetCookie(response, "Identity.Application")
						 ?? throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return ExtractCookiePair(authCookie);
	}

	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie in login page response.");
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

	private async Task<AppUserId> SeedEmployeeAsync(string userName, EmployeeRole role, string? displayName = null)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", displayName ?? userName);
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

	private async Task<RequestHoldingAreaId> SeedHoldingAreaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertNode = connection.CreateCommand();
		insertNode.CommandText = """
								 INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								 VALUES ($parentId, 'Holding area', $ownerId, $ownerId, $priorityId, $postedAt);
								 SELECT last_insert_rowid();
								 """;
		_ = insertNode.Parameters.AddWithValue("$parentId", rootId.Value);
		_ = insertNode.Parameters.AddWithValue("$ownerId", await ReadRootOwnerIdAsync(connection));
		_ = insertNode.Parameters.AddWithValue("$priorityId", PriorityMedium);
		_ = insertNode.Parameters.AddWithValue("$postedAt", DateTimeOffset.UtcNow.UtcTicks - DateTime.UnixEpoch.Ticks);
		var jobNodeId = (long)(await insertNode.ExecuteScalarAsync())!;

		await using var insertHoldingArea = connection.CreateCommand();
		insertHoldingArea.CommandText = """
										INSERT INTO request_holding_area (job_node_id, name, default_priority_id, is_active)
										VALUES ($jobNodeId, 'IT Intake', $priorityId, 1);
										SELECT last_insert_rowid();
										""";
		_ = insertHoldingArea.Parameters.AddWithValue("$jobNodeId", jobNodeId);
		_ = insertHoldingArea.Parameters.AddWithValue("$priorityId", PriorityMedium);
		var holdingAreaId = (long)(await insertHoldingArea.ExecuteScalarAsync())!;

		return new(holdingAreaId);
	}

	private static async Task<long> ReadRootOwnerIdAsync(SqliteConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT owner_user_id FROM job_node WHERE parent_id IS NULL;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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

	[GeneratedRegex("""<span class="jt-preserve-whitespace">Replace feed roller</span>\s*<span class="jt-achievement-icon jt-achievement-icon--in-progress">""")]
	private static partial Regex MyRegex();
}
