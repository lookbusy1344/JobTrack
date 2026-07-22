namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
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
using NodaTime.Text;
using Pages.Jobs;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the flat "jobs awaiting progress" dashboard: leaves only, filtered by
///     owner and/or subtree, in priority/deadline order. No per-role authorization policy, matching
///     <c>JobTreeBrowsingTests</c> — viewing job data is an unqualified baseline capability for every
///     role (spec §7.3).
/// </summary>
public sealed partial class AwaitingProgressTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	/// <summary>
	///     <c>datetime-local</c> inputs post minute precision, so a backdate assertion has to compare
	///     against a minute-aligned instant rather than "now minus N hours" with its stray seconds.
	/// </summary>
	private const string DateTimeLocalFormat = "yyyy-MM-ddTHH:mm";

	private const int MinutesPerHour = 60;
	private const int HoursBackdated = 2;
	private const int HoursBeforeFinish = 3;
	private const int HoursBeforeNowFinished = 1;

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
	public async Task A_waiting_leaf_appears_and_a_succeeded_leaf_does_not()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.basic");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var doneLeaf = await AddLeafWithWorkAsync(rootId, workerId, "Painting", adminId);
		await SetAchievementAsync(doneLeaf.JobNodeId, Achievement.InProgress, adminId, doneLeaf.Version);
		var inProgress = await seedClient.Query.GetLeafWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = doneLeaf.JobNodeId,
		});
		await SetAchievementAsync(doneLeaf.JobNodeId, Achievement.Success, adminId, inProgress.Version);
		var authCookie = await SignInAsync("awaiting.basic");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Painting");
	}

	[Fact]
	public async Task A_leaf_with_no_leaf_work_attached_appears_on_the_dashboard()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.noleafwork");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddChildAsync(rootId, workerId, "Fresh leaf awaiting assignment", adminId);
		var authCookie = await SignInAsync("awaiting.noleafwork");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fresh leaf awaiting assignment");
		body.Should().Contain("None");
	}

	[Fact]
	public async Task A_dashboard_wider_than_the_page_size_offers_a_next_page_link_that_advances_the_offset()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.paging");
		var rootId = bootstrappedRootId!.Value;
		for (var index = 0; index < AwaitingProgressModel.PageSize + 1; index++) {
			_ = await AddChildAsync(rootId, workerId, $"Leaf {index}", adminId);
		}

		var authCookie = await SignInAsync("awaiting.paging");

		var firstResponse = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var firstBody = await firstResponse.Content.ReadAsStringAsync();

		firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		firstBody.Should().Contain("Next page");
		firstBody.Should().Contain($"Offset={AwaitingProgressModel.PageSize}");

		var secondResponse = await GetAsync($"/Jobs/AwaitingProgress?offset={AwaitingProgressModel.PageSize}", authCookie);
		var secondBody = await secondResponse.Content.ReadAsStringAsync();

		secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		secondBody.Should().NotContain("Next page");
	}

	[Fact]
	public async Task A_leaf_blocked_by_an_unsatisfied_prerequisite_still_appears_marked_blocked()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.blocked");
		var rootId = bootstrappedRootId!.Value;
		var required = await AddLeafWithWorkAsync(rootId, workerId, "Required first", adminId);
		var dependent = await AddLeafWithWorkAsync(rootId, workerId, "Blocked leaf", adminId);
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = required.JobNodeId,
			DependentJobId = dependent.JobNodeId,
		});
		var authCookie = await SignInAsync("awaiting.blocked");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Blocked leaf");
		body.Should().Contain("Blocked");
	}

	[Fact]
	public async Task Starting_work_from_a_dashboard_row_advances_the_leaf_to_in_progress()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.startwork");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Fresh leaf via dashboard", adminId);
		var authCookie = await SignInAsync("awaiting.startwork");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");
		body.Should().Contain("Fresh leaf via dashboard");
		body.Should().Contain("In Progress");
	}

	[Fact]
	public async Task Filtering_by_owner_hides_another_employees_leaf()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.owner");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Worker job", adminId);
		_ = await AddLeafWithWorkAsync(rootId, adminId, "Admin job", adminId);
		var authCookie = await SignInAsync("awaiting.owner");

		var response = await GetAsync($"/Jobs/AwaitingProgress?ownerUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker job");
		body.Should().NotContain("Admin job");
	}

	[Fact]
	public async Task AwaitingProgress_remembers_the_owner_filter_across_a_return_visit()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.filtermem");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Worker job", adminId);
		_ = await AddLeafWithWorkAsync(rootId, adminId, "Admin job", adminId);
		var authCookie = await SignInAsync("awaiting.filtermem");

		// Explicitly filter to the worker; capture the session that now remembers the choice.
		using var chooseRequest = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/AwaitingProgress?ownerUserId={workerId.Value}");
		chooseRequest.Headers.Add("Cookie", authCookie);
		var chooseResponse = await client.SendAsync(chooseRequest);
		var sessionCookie = ExtractCookiePair(
			FindSetCookie(chooseResponse, "JobTrack.Session") ?? throw new InvalidOperationException("No session cookie was set."));

		// Return with no owner param: the remembered worker filter still applies.
		using var returnRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		returnRequest.Headers.Add("Cookie", $"{authCookie}; {sessionCookie}");
		var returnResponse = await client.SendAsync(returnRequest);
		var body = await returnResponse.Content.ReadAsStringAsync();

		returnResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker job");
		body.Should().NotContain("Admin job");
	}

	[Fact]
	public async Task AwaitingProgress_defaults_to_all_owners_when_nothing_is_remembered()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.default-all");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Worker job", adminId);
		_ = await AddLeafWithWorkAsync(rootId, adminId, "Admin job", adminId);
		var authCookie = await SignInAsync("awaiting.default-all");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker job");
		body.Should().Contain("Admin job", "with nothing remembered the dashboard defaults to every owner");
	}

	[Fact]
	public async Task Scoping_to_a_subtree_hides_a_leaf_outside_it()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.subtree");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Outside the branch", adminId);
		var authCookie = await SignInAsync("awaiting.subtree");

		var response = await GetAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Outside the branch");
	}

	[Fact]
	public async Task A_leaf_with_an_active_session_shows_an_end_session_link_instead_of_start()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.toggle");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Toggle leaf", adminId);
		var authCookie = await SignInAsync("awaiting.toggle");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, formCookie, token, leafId);

		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(startResponse, authCookie);
		var startBody = await reloaded.Content.ReadAsStringAsync();
		// Plan §5.3: the dashboard row never finishes inline -- the viewer's own one-click Start
		// is replaced by an "End session" link into /Jobs/Work, not an inline finish form.
		startBody.Should().Contain($"/Jobs/Work?leafNodeId={leafId.Value}");
		startBody.Should().Contain("End session");
		startBody.Should().NotContain("title=\"Start session\"");
	}

	[Fact]
	/// <summary>
	/// The active-session pill has its own column here too, in the slot Priority used to hold, so the
	/// dashboard and Browse's subtree read the same way rather than each putting the pill somewhere
	/// different.
	/// </summary>
	public async Task The_active_session_pill_has_its_own_column_in_place_of_priority()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.activecolumn");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Active column leaf", adminId);
		var authCookie = await SignInAsync("awaiting.activecolumn");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, formCookie, token, leafId);
		var reloaded = await FollowRedirectAsync(startResponse, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();

		body.Should().Contain("<th class=\"jt-col-active\">Active</th>");
		body.Should().NotContain(">Priority</th>");
		body.Should().Contain("Active since");
	}

	[Fact]
	public async Task Finishing_work_from_the_dashboard_returns_the_row_to_a_start_button()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.finish");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Finish leaf", adminId);
		var authCookie = await SignInAsync("awaiting.finish");

		var (startFormCookie, startToken) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, startFormCookie, startToken, leafId);
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		await FollowRedirectAsync(startResponse, authCookie);
		var session = (await GetSessionsAsync(leafId, adminId)).Should().ContainSingle().Subject;

		var (workFormCookie, workToken) = await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leafId.Value}");
		var finishResponse = await PostFinishWorkAsync(authCookie, workFormCookie, workToken, leafId, session.Id.Value, session.Version);
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();

		finishBody.Should().Contain("Ends this session; the job stays In Progress.");
		finishBody.Should().Contain("#jt-icon-start");

		using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		dashboardRequest.Headers.Add("Cookie", authCookie);
		var dashboardResponse = await client.SendAsync(dashboardRequest);
		var dashboardBody = await dashboardResponse.Content.ReadAsStringAsync();
		dashboardBody.Should().Contain("title=\"Start session\"");
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_time_from_the_dashboard_row()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.backdate-start");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated start leaf", adminId);
		var authCookie = await SignInAsync("awaiting.backdate-start");
		var backdatedAt = MinutesAgo(HoursBackdated * MinutesPerHour);

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, FormatForDateTimeLocal(backdatedAt));

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");
		body.Should().Contain("End session");

		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(Instant.FromDateTimeOffset(backdatedAt));
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_time_from_the_dashboard_row_shows_a_helpful_error()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.future-start");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Future start leaf", adminId);
		var authCookie = await SignInAsync("awaiting.future-start");
		var future = FormatForDateTimeLocal(MinutesAgo(-HoursBackdated * MinutesPerHour));

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, future);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("in the future");
	}

	[Fact]
	public async Task Starting_a_session_with_a_malformed_backdate_from_the_dashboard_row_does_not_start_work()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.malformed-start");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Malformed dashboard start", adminId);
		var authCookie = await SignInAsync("awaiting.malformed-start");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, "not-a-local-date-time");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid date and time.");
		(await GetSessionsAsync(leafId, adminId)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_worker_can_finish_a_session_with_a_backdated_time_reached_from_the_dashboard_row()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.backdate-finish");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated finish leaf", adminId);
		var authCookie = await SignInAsync("awaiting.backdate-finish");
		var startedAt = MinutesAgo(HoursBeforeFinish * MinutesPerHour);
		var finishedAt = MinutesAgo(HoursBeforeNowFinished * MinutesPerHour);

		var (startFormCookie, startToken) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, startFormCookie, startToken, leafId, FormatForDateTimeLocal(startedAt));
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		await FollowRedirectAsync(startResponse, authCookie);
		var session = (await GetSessionsAsync(leafId, adminId)).Should().ContainSingle().Subject;

		var (workFormCookie, workToken) = await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leafId.Value}");
		var finishResponse =
			await PostFinishWorkAsync(authCookie, workFormCookie, workToken, leafId, session.Id.Value, session.Version,
				FormatForDateTimeLocal(finishedAt));
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();
		finishBody.Should().Contain("Ends this session; the job stays In Progress.");

		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.FinishedAt.Should().Be(Instant.FromDateTimeOffset(finishedAt));
	}

	[Fact]
	// Auckland is deep in southern winter (NZST, UTC+12, no DST) in June, and never coincides with
	// whatever zone the test process's own machine happens to run in -- so this proves the backdate
	// was resolved in the *employee's own* zone, not the server's.
	public async Task Backdating_and_viewing_a_session_both_use_the_viewing_employees_own_zone_not_the_servers()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.tz-auckland", "Pacific/Auckland");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated in Auckland's own zone", adminId);
		var authCookie = await SignInAsync("awaiting.tz-auckland");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, "2026-06-15T09:00");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(Instant.FromUtc(2026, 6, 14, 21, 0),
			"09:00 NZST (UTC+12) on 15 June is 21:00 UTC the day before");

		// Reloading the dashboard as the same Auckland-zoned employee must show the wall clock back
		// converted through the same zone as the write, not UTC. The "Active since" pill only shows a
		// compact date (InstantDisplay.FormatCompact) for a non-today session, dropping the time --
		// but 15 June is still the proof: in UTC this instant falls on the 14th, so only a correct
		// Auckland conversion produces the 15th.
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("15 Jun");
	}

	[Fact]
	// The UK's spring-forward transition is a fixed EU-wide rule (last Sunday in March, 01:00 UTC), so
	// 01:00-01:59 local time on 2026-03-29 never occurs -- this proves a backdate landing in that gap
	// is resolved through the same CivilTimeResolver policy (ADR 0008) as the rest of the app, not a
	// naive parse that would throw or silently pick an arbitrary instant.
	public async Task Backdating_into_a_dst_gap_resolves_via_the_shared_civil_time_resolver()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.tz-dst-gap", "Europe/London");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated into a spring-forward gap", adminId);
		var authCookie = await SignInAsync("awaiting.tz-dst-gap");

		const string gapLocalWallClock = "2026-03-29T01:30";
		var londonZone = DateTimeZoneProviders.Tzdb["Europe/London"];
		var expected = CivilTimeResolver.ToInstant(
			LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-dd'T'HH:mm").Parse(gapLocalWallClock).Value, londonZone);

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, gapLocalWallClock);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(expected);
	}

	[Fact]
	public async Task The_dashboard_row_offers_start_as_an_icon_beside_a_backdate_disclosure()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.icons");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddChildAsync(rootId, workerId, "Icon row leaf", adminId);
		var authCookie = await SignInAsync("awaiting.icons");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("#jt-icon-start");
		body.Should().Contain("#jt-icon-backdate");
		body.Should().Contain("name=\"startedAt\"");
	}

	[Fact]
	public async Task The_dashboard_row_offers_finish_as_an_icon_once_a_session_is_active()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.finish-icon");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Finish icon leaf", adminId);
		var authCookie = await SignInAsync("awaiting.finish-icon");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("#jt-icon-finish");
		body.Should().NotContain("btn btn-secondary\">Finish / pause");
	}

	[Fact]
	public async Task The_dashboard_shows_current_costs_for_paused_and_in_progress_jobs()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.costs");
		_ = await SeedWorkerEmployeeAsync("awaiting.costs.viewer", EmployeeRole.CostViewer);
		var rootId = bootstrappedRootId!.Value;
		var pausedLeaf = await AddChildAsync(rootId, workerId, "Paused costed leaf", adminId);
		var activeLeaf = await AddChildAsync(rootId, workerId, "Active costed leaf", adminId);
		await AttachLeafWorkAsync(pausedLeaf, adminId);
		await AttachLeafWorkAsync(activeLeaf, adminId);

		var now = SystemClock.Instance.GetCurrentInstant();
		await AddWorkingWindowAsync(workerId, adminId, now - Duration.FromDays(1), now - Duration.FromDays(1) + Duration.FromHours(9));
		await AddWorkingWindowAsync(workerId, adminId, now - Duration.FromHours(2), now + Duration.FromHours(1));
		await AddUserCostRateAsync(workerId, adminId, 25m, now - Duration.FromDays(2));
		await AddFinishedSessionAsync(workerId, pausedLeaf, now - Duration.FromDays(1), now - Duration.FromDays(1) + Duration.FromHours(8));
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = activeLeaf,
			WorkedByUserId = workerId,
			StartedAt = now - Duration.FromHours(1),
		});
		var authCookie = await SignInAsync("awaiting.costs.viewer");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Paused costed leaf");
		body.Should().Contain("Active costed leaf");
		body.Should().Contain(">&#xA3;200.00<");

		// The active leaf's session is still running: its accrued cost grows with real elapsed time
		// between the `now` captured above and this request actually rendering, so an exact string match
		// is a wall-clock race. £25/hour accrues a penny every 1.44s of drift, so tolerate a few minutes
		// of slack rather than pinning an exact value.
		var activeLeafCosts = MoneyAmountPattern().Matches(body)
			.Select(match => decimal.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture));
		activeLeafCosts.Should().Contain(amount => amount >= 25.00m && amount <= 25.50m);
	}

	[Fact]
	public async Task An_unauthenticated_request_is_redirected_to_sign_in()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	private async Task<(AppUserId AdministratorId, AppUserId WorkerId)> BootstrapAndSeedWorkerAsync(
		string workerUserName, string workerIanaTimeZone = "UTC")
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

		var workerId = await SeedWorkerEmployeeAsync(workerUserName, ianaTimeZone: workerIanaTimeZone);

		return (bootstrapResult.AdministratorId, workerId);
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<LeafWorkResult> AddLeafWithWorkAsync(JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id });
	}

	private async Task AttachLeafWorkAsync(JobNodeId leafId, AppUserId adminId) =>
		_ = await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() }, JobNodeId = leafId });

	private async Task AddWorkingWindowAsync(AppUserId workerId, AppUserId adminId, Instant start, Instant end) =>
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(start, end),
				null),
			Reason = "Working window for awaiting-progress cost test",
		});

	private async Task AddUserCostRateAsync(AppUserId workerId, AppUserId adminId, decimal amountPerHour, Instant effectiveStart) =>
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Rate = new(new(amountPerHour), effectiveStart, null),
		});

	private async Task AddFinishedSessionAsync(AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
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

	private async Task SetAchievementAsync(JobNodeId leafId, Achievement newAchievement, AppUserId adminId, long version) =>
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			NewAchievement = newAchievement,
			Reason = "Test transition",
			Version = version,
		});

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

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
	}

	/// <summary>
	///     A minute-aligned UTC wall time, <paramref name="minutes" /> ago. UTC, not the test process's
	///     own local zone, because a <c>datetime-local</c> backdate posts a bare wall time with no
	///     offset and is now resolved in the *viewing employee's own* zone (<c>BackdateInstant</c>,
	///     <c>IViewerTimeZoneResolver</c>) — this suite's worker is seeded with
	///     <c>
	///         iana_time_zone =
	///         'UTC'
	///     </c>
	///     (<see cref="SeedWorkerEmployeeAsync" />), so a UTC-based wall time round-trips
	///     regardless of what zone the test process itself happens to run in.
	/// </summary>
	private static DateTimeOffset MinutesAgo(int minutes)
	{
		var now = DateTimeOffset.UtcNow;

		return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(-minutes);
	}

	private static string FormatForDateTimeLocal(DateTimeOffset value) => value.ToString(DateTimeLocalFormat, CultureInfo.InvariantCulture);

	private async Task<EquatableArray<WorkSessionResult>> GetSessionsAsync(JobNodeId leafId, AppUserId actor) =>
		await seedClient.Query.GetLeafSessionsAsync(
			new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, LeafWorkId = leafId },
			CancellationToken.None);

	private async Task<HttpResponseMessage> PostStartWorkAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId jobNodeId,
		string? startedAt = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/AwaitingProgress?handler=StartWork");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["jobNodeId"] = jobNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (startedAt is not null) {
			fields["startedAt"] = startedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	/// <summary>
	///     Ending a session from the dashboard is a two-step navigation (plan §5.3): the row's "End
	///     session" link opens <c>/Jobs/Work</c>, whose own Finish handler actually posts the finish.
	///     This mirrors that by posting directly to <c>/Jobs/Work?handler=Finish</c>, the same handler
	///     the dashboard's End-session link ultimately drives.
	/// </summary>
	private async Task<HttpResponseMessage> PostFinishWorkAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId,
		long sessionId, long version, string? finishedAt = null)
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

	[GeneratedRegex(">&#xA3;(?<amount>[0-9]+\\.[0-9]{2})<")]
	private static partial Regex MoneyAmountPattern();

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private async Task<AppUserId> SeedWorkerEmployeeAsync(string userName, EmployeeRole role = EmployeeRole.Worker, string ianaTimeZone = "UTC")
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, $ianaTimeZone); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		_ = insertAppUser.Parameters.AddWithValue("$ianaTimeZone", ianaTimeZone);
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
