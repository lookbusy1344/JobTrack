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
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for leaf work and session start/pause/resume/finish, plus audited correction
///     (plan §8.5 slice 4). "Pause"/"resume" are UI terms posting to the same Finish/Start handlers as
///     stop/start (spec §4.4).
/// </summary>
public sealed partial class LeafWorkTests : IAsyncLifetime, IDisposable
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
			UserName = "admin.work-tests",
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
	public async Task A_worker_can_start_work_on_a_fresh_leaf_in_one_click()
	{
		var workerId = await SeedEmployeeAsync("work.starter", EmployeeRole.Worker);
		var leaf = await AddChildAsync(rootId, workerId, "Pour foundation");
		var authCookie = await SignInAsync("work.starter");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		startBody.Should().Contain("Session started");
		startBody.Should().Contain("Active");
	}

	[Fact]
	public async Task Starting_a_session_saves_the_write_up_typed_beside_it()
	{
		// Start's own handler carries no write-up fields of its own (the architecture rule against a
		// handler coordinating more than one IJobTrackClient mutation) -- site.js instead fires a
		// separate SaveWriteUp request before submitting Start, which this reproduces as the two
		// requests it actually is.
		var workerId = await SeedEmployeeAsync("work.start-writeup", EmployeeRole.Worker);
		var leaf = await AddChildAsync(rootId, workerId, "Pour foundation with write-up");
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});
		var authCookie = await SignInAsync("work.start-writeup");

		var (writeUpCookie, writeUpToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var writeUpResponse = await PostSaveWriteUpAsync(
			authCookie, writeUpCookie, writeUpToken, leaf.Id, leaf.Version, "Foundation formwork is square and level.");
		writeUpResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id });
		current.Node.WriteUp.Should().Be("Foundation formwork is square and level.");
	}

	[Fact]
	public async Task Starting_work_on_the_root_shows_a_helpful_error()
	{
		var jobManagerId = await SeedEmployeeAsync("work.root-error", EmployeeRole.JobManager);
		var authCookie = await SignInAsync("work.root-error");

		var (cookie, token) = await GetWorkFormAsync(authCookie, rootId, jobManagerId);
		var response = await PostAsync("Start", authCookie, cookie, token, rootId, jobManagerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("cannot hold LeafWork");
	}

	[Fact]
	public async Task A_worker_can_finish_their_own_active_session()
	{
		var workerId = await SeedEmployeeAsync("work.finisher", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Frame walls");
		var authCookie = await SignInAsync("work.finisher");

		var (startCookie, startToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, startCookie, startToken, leaf.Id, workerId);
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		var (sessionId, version) = ExtractFirstSession(startBody);

		var (finishCookie, finishToken) = await ExtractFormAsync(startReloaded, startCookie);
		var finishResponse = await PostFinishAsync(authCookie, finishCookie, finishToken, leaf.Id, workerId, sessionId, version);
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();
		finishBody.Should().Contain("Ends this session; the job stays In Progress.");
	}

	[Fact]
	public async Task The_leaf_toolbar_shows_finish_instead_of_start_once_a_session_is_active()
	{
		var workerId = await SeedEmployeeAsync("work.toggle", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Toggle toolbar leaf");
		var authCookie = await SignInAsync("work.toggle");

		var beforeResponse = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);
		var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
		beforeBody.Should().Contain("#jt-icon-start");
		beforeBody.Should().Contain("Start session");
		beforeBody.Should().NotContain("Start work");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		startBody.Should().Contain("Pause job");
		startBody.Should().NotContain("Finish / pause");
		// Plan §4.1: the viewer's own one-click Start is replaced by Pause job, but the
		// authorized "Start for..." disclosure (also drawn with jt-icon-start) for another worker is
		// never removed -- only the viewer's own primary action toggles.
		startBody.Should().NotContain("title=\"Start session\"");
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_time_from_the_leaf_toolbar()
	{
		var workerId = await SeedEmployeeAsync("work.backdate-starter", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Backdated start leaf");
		var authCookie = await SignInAsync("work.backdate-starter");
		var backdatedAt = MinutesAgo(HoursBackdated * MinutesPerHour);

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId, FormatForDateTimeLocal(backdatedAt));

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");

		var sessions = await GetSessionsAsync(leaf.Id);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(Instant.FromDateTimeOffset(backdatedAt));
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_time_from_the_leaf_toolbar_shows_a_helpful_error()
	{
		var workerId = await SeedEmployeeAsync("work.future-starter", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Future start leaf");
		var authCookie = await SignInAsync("work.future-starter");
		var future = FormatForDateTimeLocal(MinutesAgo(-HoursBackdated * MinutesPerHour));

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId, future);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("in the future");
	}

	[Fact]
	public async Task Starting_a_session_with_a_malformed_backdate_from_the_leaf_toolbar_does_not_start_work()
	{
		var workerId = await SeedEmployeeAsync("work.malformed-starter", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Malformed start leaf");
		var authCookie = await SignInAsync("work.malformed-starter");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId, "not-a-local-date-time");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid date and time.");
		(await GetSessionsAsync(leaf.Id)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_worker_can_finish_a_session_with_a_backdated_time_from_the_sessions_panel()
	{
		var workerId = await SeedEmployeeAsync("work.backdate-finisher", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Backdated finish leaf");
		var authCookie = await SignInAsync("work.backdate-finisher");
		var startedAt = MinutesAgo(HoursBeforeFinish * MinutesPerHour);
		var finishedAt = MinutesAgo(HoursBeforeNowFinished * MinutesPerHour);

		var (startCookie, startToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, startCookie, startToken, leaf.Id, workerId, FormatForDateTimeLocal(startedAt));
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		var (sessionId, version) = ExtractFirstSession(startBody);

		var (finishCookie, finishToken) = await ExtractFormAsync(startReloaded, startCookie);
		var finishResponse = await PostFinishAsync(
			authCookie, finishCookie, finishToken, leaf.Id, workerId, sessionId, version, FormatForDateTimeLocal(finishedAt));
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();
		finishBody.Should().Contain("Ends this session; the job stays In Progress.");

		var sessions = await GetSessionsAsync(leaf.Id);
		sessions.Should().ContainSingle().Which.FinishedAt.Should().Be(Instant.FromDateTimeOffset(finishedAt));
	}

	[Fact]
	public async Task The_sessions_panel_offers_pause_as_an_icon_and_uses_the_explicit_outcome_label()
	{
		var workerId = await SeedEmployeeAsync("work.finish-icon", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Finish icon session leaf");
		var authCookie = await SignInAsync("work.finish-icon");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("#jt-icon-finish");
		// Both the icon-only row action and the page toolbar name the outcome consistently.
		body.Should().Contain("Pause job");
		body.Should().NotContain("Finish / pause");
	}

	[Fact]
	public async Task The_leaf_toolbar_offers_a_backdate_disclosure_beside_start()
	{
		var workerId = await SeedEmployeeAsync("work.backdate-disclosure", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Backdate disclosure leaf");
		var authCookie = await SignInAsync("work.backdate-disclosure");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("#jt-icon-backdate");
		body.Should().Contain("name=\"startedAt\"");
	}

	[Fact]
	// ADR 0041: recorded work is job data, viewable by every employee role (spec §7.3), so a Worker
	// may read another worker's sessions. Editing one stays gated by node control (CanManage), which
	// this does not change.
	public async Task A_worker_can_view_another_workers_sessions()
	{
		var workerId = await SeedEmployeeAsync("work.owner", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("work.snooper", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Private work");
		var authCookie = await SignInAsync("work.snooper");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("may not view");
		_ = otherWorkerId;
	}

	[Fact]
	public async Task An_administrator_can_correct_a_workers_historical_session_with_a_reason()
	{
		var workerId = await SeedEmployeeAsync("work.correctable", EmployeeRole.Worker);
		var managerId = await SeedEmployeeAsync("work.correcting-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Correctable work");

		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
		});

		var authCookie = await SignInAsync("work.correcting-manager");

		var (getCookie, getToken) = await GetCorrectFormAsync(authCookie, leaf.Id, workerId, finished.Id);
		var correctResponse = await PostCorrectAsync(
			authCookie, getCookie, getToken, leaf.Id, workerId, finished.Id,
			"2026-01-01T09:00", "2026-01-01T10:00", "Forgot to clock out on time.");

		correctResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		// Returns to Work without forcing the corrected worker as the filter -- Work restores the
		// viewer's remembered choice (or the Everyone default) instead.
		correctResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Work").And.NotContain("orkedByUserId");
	}

	[Fact]
	public async Task Work_defaults_to_everyone_when_the_viewer_may_manage_the_leaf()
	{
		var ownerId = await SeedEmployeeAsync("work.default-all.owner", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Default-all leaf");
		var authCookie = await SignInAsync("work.default-all.owner");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("Sessions worked by",
			"the unfiltered Everyone view is always the default, whether or not the viewer may manage the leaf");
	}

	[Fact]
	public async Task Work_defaults_to_everyone_even_when_the_viewer_may_not_manage_the_leaf()
	{
		var ownerId = await SeedEmployeeAsync("work.default-self.owner", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("work.default-self.viewer", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Default-self leaf");
		var authCookie = await SignInAsync("work.default-self.viewer");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("Sessions worked by",
			"WorkSessionAccessPolicy.CanView (ADR 0041) grants every baseline role unqualified visibility of all sessions, " +
			"so there is no permission reason to default a non-managing viewer to their own sessions only");
	}

	[Fact]
	public async Task Work_remembers_the_last_chosen_worker_filter_across_a_return_visit()
	{
		var ownerId = await SeedEmployeeAsync("work.filtermem.owner", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("work.filtermem.other", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Filter memory leaf");
		var authCookie = await SignInAsync("work.filtermem.owner");

		// Explicitly filter to the other worker; capture the session that now remembers the choice.
		using var chooseRequest = new HttpRequestMessage(
			HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}&WorkedByUserId={otherWorkerId.Value}");
		chooseRequest.Headers.Add("Cookie", authCookie);
		var chooseResponse = await client.SendAsync(chooseRequest);
		var sessionCookie = ExtractCookiePair(
			FindSetCookie(chooseResponse, "JobTrack.Session") ?? throw new InvalidOperationException("No session cookie was set."));

		// Return with no filter param: the remembered worker applies, even though the owner's default
		// would otherwise be Everyone.
		using var returnRequest = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		returnRequest.Headers.Add("Cookie", $"{authCookie}; {sessionCookie}");
		var returnResponse = await client.SendAsync(returnRequest);
		var body = await returnResponse.Content.ReadAsStringAsync();

		returnResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Sessions worked by work.filtermem.other");
	}

	[Fact]
	public async Task Clear_finished_time_reopens_the_session_in_one_step_when_a_reason_is_given()
	{
		var workerId = await SeedEmployeeAsync("work.clearfinish", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("work.clearfinish-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Clear finish work");
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
		});
		var authCookie = await SignInAsync("work.clearfinish-manager");

		var (cookie, token) = await GetCorrectFormAsync(authCookie, leaf.Id, workerId, finished.Id);
		var response = await PostClearFinishAsync(
			authCookie, cookie, token, leaf.Id, workerId, finished.Id, "2026-01-01T09:00", "Left the session running.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Work");
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().BeNull();
	}

	[Fact]
	public async Task Clear_finished_time_still_requires_a_reason()
	{
		var workerId = await SeedEmployeeAsync("work.clearfinish-noreason", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("work.clearfinish-noreason-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Clear finish no reason work");
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
		});
		var authCookie = await SignInAsync("work.clearfinish-noreason-manager");

		var (cookie, token) = await GetCorrectFormAsync(authCookie, leaf.Id, workerId, finished.Id);
		var response = await PostClearFinishAsync(
			authCookie, cookie, token, leaf.Id, workerId, finished.Id, "2026-01-01T09:00", string.Empty);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Correcting_a_session_with_a_malformed_optional_finish_does_not_reopen_it()
	{
		var workerId = await SeedEmployeeAsync("work.malformed-correction", EmployeeRole.Worker);
		var managerId = await SeedEmployeeAsync("work.malformed-correcting-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Malformed correction work");
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
		});
		var authCookie = await SignInAsync("work.malformed-correcting-manager");

		var (cookie, token) = await GetCorrectFormAsync(authCookie, leaf.Id, workerId, finished.Id);
		var response = await PostCorrectAsync(
			authCookie, cookie, token, leaf.Id, workerId, finished.Id,
			"2026-01-01T09:00", "not-a-local-date-time", "Correcting malformed input must fail.");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid date and time.");
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task A_viewer_with_an_unrecognized_persisted_time_zone_is_not_silently_treated_as_utc()
	{
		var workerId = await SeedEmployeeAsync("work.unknown-zone", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Unknown zone leaf");
		await using (var connection = new SqliteConnection(database.ConnectionString)) {
			await connection.OpenAsync();
			await using var command = connection.CreateCommand();
			command.CommandText = "UPDATE app_user SET iana_time_zone = 'Etc/No_Such_Zone' WHERE id = $id;";
			_ = command.Parameters.AddWithValue("$id", workerId.Value);
			_ = await command.ExecuteNonQueryAsync();
		}

		var authCookie = await SignInAsync("work.unknown-zone");
		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
	}

	[Fact]
	public async Task A_controlling_worker_can_finish_sessions_and_record_a_non_success_outcome_via_the_completion_dropdown()
	{
		var workerId = await SeedEmployeeAsync("work.complete-cancelled", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Site access withdrawn");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await SignInAsync("work.complete-cancelled");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)], finalAchievement: Achievement.Cancelled);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job cancelled and session finished.");
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id }, CancellationToken.None);
		leafWork.Achievement.Should().Be(Achievement.Cancelled);
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task A_controlling_worker_can_complete_a_job_with_one_active_session_from_the_work_page()
	{
		var workerId = await SeedEmployeeAsync("work.complete-one", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await SignInAsync("work.complete-one");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job completed and session finished.");
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id }, CancellationToken.None);
		leafWork.Achievement.Should().Be(Achievement.Success);
	}

	[Fact]
	public async Task Completion_options_backdate_the_active_set_and_persist_the_optional_note()
	{
		var workerId = await SeedEmployeeAsync("work.complete-options", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Complete with options");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
			StartedAt = Instant.FromDateTimeOffset(MinutesAgo(HoursBeforeFinish * MinutesPerHour)),
		});
		var finishedAt = MinutesAgo(HoursBeforeNowFinished * MinutesPerHour);
		var authCookie = await SignInAsync("work.complete-options");

		var page = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var pageBody = await page.Content.ReadAsStringAsync();
		pageBody.Should().Contain("Completion options");
		pageBody.Should().Contain("name=\"completionFinishedAt\"");
		pageBody.Should().Contain("name=\"completionNote\"");
		var (cookie, token) = await ExtractFormAsync(page, FindSetCookie(page, "Antiforgery") is string setCookie
			? ExtractCookiePair(setCookie)
			: throw new InvalidOperationException("No antiforgery cookie in Work response."));
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)],
			FormatForDateTimeLocal(finishedAt), "Customer confirmed acceptance");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().Be(Instant.FromDateTimeOffset(finishedAt));
		var audit = await seedClient.Audit.SearchAuditEventsAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			Filter = new() { EntityType = "leaf_work", EntityId = leaf.Id.Value },
		});
		audit.Events.Should().Contain(entry => entry.Reason == "Completed from the leaf work page (Customer confirmed acceptance)");
	}

	[Fact]
	public async Task Completing_a_job_with_two_active_sessions_finishes_both_and_records_success()
	{
		var workerId = await SeedEmployeeAsync("work.complete-two", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("work.complete-two-other", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets, two workers");
		var first = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var second = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = otherWorkerId,
		});
		var authCookie = await SignInAsync("work.complete-two");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(first.Id.Value, first.Version), (second.Id.Value, second.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job completed and 2 sessions finished.");
		(await GetSessionsAsync(leaf.Id)).Should().OnlyContain(s => s.FinishedAt != null);
	}

	[Fact]
	public async Task Several_active_sessions_show_an_always_expanded_completion_review_without_repeating_the_sessions_list()
	{
		var managerId = await SeedEmployeeAsync("work.review-manager", EmployeeRole.JobManager);
		var firstWorkerId = await SeedEmployeeAsync("work.review-first", EmployeeRole.Worker);
		var secondWorkerId = await SeedEmployeeAsync("work.review-second", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Review several sessions");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = managerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = firstWorkerId,
		});
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = managerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = secondWorkerId,
		});
		var authCookie = await SignInAsync("work.review-manager");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("<div class=\"jt-completion-review mt-3\"");
		body.Should().Contain("Finish 2 sessions and complete job");
		body.Should().NotContain("active since", "the per-session worker/start-time list duplicated the Sessions table below it");
	}

	[Theory]
	[InlineData(Achievement.Cancelled, "Cancelled. To record more work")]
	[InlineData(Achievement.Unsuccessful, "Unsuccessful. To record more work")]
	public async Task A_terminal_leaf_names_its_actual_outcome(Achievement achievement, string expectedCopy)
	{
		var managerId = await SeedEmployeeAsync($"work.outcome-{achievement}", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, $"{achievement} leaf");
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = managerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			NewAchievement = achievement,
			Reason = "Closing for copy test",
			Version = 1,
		});
		var authCookie = await SignInAsync($"work.outcome-{achievement}");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain(expectedCopy);
		body.Should().NotContain("<p>Completed. To record more work");
		body.Should().Contain($"selected=\"selected\" value=\"{managerId.Value}\"", "the reopen target must default to the viewer");
	}

	[Fact]
	public async Task A_non_controlling_worker_cannot_complete_a_job_from_the_work_page()
	{
		var workerId = await SeedEmployeeAsync("work.complete-forbidden-owner", EmployeeRole.Worker);
		var strangerId = await SeedEmployeeAsync("work.complete-forbidden-stranger", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await SignInAsync("work.complete-forbidden-stranger");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, strangerId);
		var response = await PostCompleteAsync(authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_non_controlling_worker_sees_no_doomed_start_or_outcome_controls()
	{
		var ownerId = await SeedEmployeeAsync("work.capabilities-owner", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("work.capabilities-bystander", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Capability-gated leaf");
		var authCookie = await SignInAsync("work.capabilities-bystander");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain(">Start session</button>");
		body.Should().NotContain(">Cancel job</button>");
		body.Should().NotContain(">Mark unsuccessful</button>");
		body.Should().Contain("A controlling owner, Job Manager, or Administrator can start work on this job.");
	}

	[Fact]
	public async Task A_prior_participant_can_reopen_and_start_for_themselves_from_the_work_page()
	{
		var workerId = await SeedEmployeeAsync("work.reopen-participant", EmployeeRole.Worker);
		var newOwnerId = await SeedEmployeeAsync("work.reopen-new-owner", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Terminal leaf");
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
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not work out",
			Version = 2,
		});
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = leaf.Id,
			Description = leaf.Description,
			OwnerUserId = newOwnerId,
			Priority = Priority.Medium,
			Version = leaf.Version,
		});
		var authCookie = await SignInAsync("work.reopen-participant");
		var pageResponse = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var pageBody = await pageResponse.Content.ReadAsStringAsync();
		pageBody.Should().Contain(">Reopen and start session</button>");
		pageBody.Should().NotContain(">Reopen without starting</button>");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostReopenAndStartAsync(authCookie, cookie, token, leaf.Id, 3, "Work resumed", workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job reopened. Session started.");
	}

	[Fact]
	public async Task Reopening_and_starting_for_yourself_does_not_pin_the_sessions_filter_to_yourself()
	{
		var workerId = await SeedEmployeeAsync("work.reopen-filter", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Terminal leaf for filter check");
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
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not work out",
			Version = 2,
		});
		var authCookie = await SignInAsync("work.reopen-filter");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostReopenAndStartAsync(authCookie, cookie, token, leaf.Id, 2, "Work resumed", workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		// The reopened-for worker (here, the actor themselves) must never leak into the Sessions
		// filter -- the "workedByUserId" name collision this regresses used to carry the actor's own
		// id onto the redirect as an explicit filter value.
		response.Headers.Location!.OriginalString.Should().NotContain("orkedByUserId");
		var body = await (await FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().NotContain("Sessions worked by", "the unfiltered Everyone view must survive reopening for yourself");
	}

	[Fact]
	public async Task Reopening_and_starting_a_session_saves_the_write_up_typed_beside_it()
	{
		var workerId = await SeedEmployeeAsync("work.reopen-writeup", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Cancelled leaf with write-up");
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
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Cancelled,
			Reason = "Client changed their mind",
			Version = 2,
		});
		var authCookie = await SignInAsync("work.reopen-writeup");

		var (writeUpCookie, writeUpToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var writeUpResponse = await PostSaveWriteUpAsync(
			authCookie, writeUpCookie, writeUpToken, leaf.Id, leaf.Version, "Client reinstated the original scope.");
		writeUpResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostReopenAndStartAsync(
			authCookie, cookie, token, leaf.Id, 2, "Client changed their mind again", workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id });
		current.Node.WriteUp.Should().Be("Client reinstated the original scope.");
	}

	[Fact]
	public async Task Changing_the_outcome_saves_the_write_up_typed_beside_it()
	{
		var workerId = await SeedEmployeeAsync("work.outcome-writeup", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Leaf for outcome write-up");
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
		var authCookie = await SignInAsync("work.outcome-writeup");

		var (writeUpCookie, writeUpToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var writeUpResponse = await PostSaveWriteUpAsync(
			authCookie, writeUpCookie, writeUpToken, leaf.Id, leaf.Version, "Superseded; see the replacement job for details.");
		writeUpResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=SetAchievement");
		request.Headers.Add("Cookie", $"{authCookie}; {cookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leaf.Id.Value.ToString(CultureInfo.InvariantCulture),
			["leafWorkVersion"] = "2",
			["newAchievement"] = nameof(Achievement.Cancelled),
			["reason"] = "Superseded by another job",
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id });
		current.Node.WriteUp.Should().Be("Superseded; see the replacement job for details.");
	}

	[Fact]
	public async Task The_work_page_shows_the_leafs_current_write_up_in_a_prominent_multi_line_field()
	{
		var workerId = await SeedEmployeeAsync("work.writeup-shown", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Write-up leaf");
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = leaf.Id,
			Description = leaf.Description,
			WriteUp = "Existing notes from a prior worker.",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			Version = leaf.Version,
		});
		var authCookie = await SignInAsync("work.writeup-shown");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("id=\"writeup\"");
		body.Should().Contain("<textarea id=\"writeUp\"");
		body.Should().Contain("rows=\"6\"", "the write-up field is multi-line, not a single-line input");
		body.Should().Contain("Existing notes from a prior worker.");
	}

	[Fact]
	public async Task The_ending_section_carries_pause_complete_and_save_write_up_in_one_form()
	{
		var workerId = await SeedEmployeeAsync("work.ending-one-form", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "One ending form");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await SignInAsync("work.ending-one-form");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var section = body[body.IndexOf("id=\"end-session\"", StringComparison.Ordinal)..];
		section = section[..section.IndexOf("</form>", StringComparison.Ordinal)];
		section.Should().Contain("<textarea id=\"writeUp\"", "the write-up posts with whichever ending button is pressed");
		section.Should().Contain("Pause job");
		section.Should().Contain("Complete job");
		section.Should().Contain("Save write-up");
	}

	/// <summary>
	///     A paused leaf (<c>InProgress</c>, nobody clocked on) is a valid, expected state — ADR 0045
	///     allows zero active sessions from <c>InProgress</c>, and Pause job produces exactly this. The
	///     page names it rather than looking identical to a leaf nobody has started, and still offers
	///     the ending decision, since completing from zero sessions is the supported path.
	/// </summary>
	[Fact]
	public async Task A_paused_leaf_reads_as_paused_and_can_still_be_completed_with_its_write_up()
	{
		var workerId = await SeedEmployeeAsync("work.paused", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Paused leaf");
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
		var authCookie = await SignInAsync("work.paused");

		var response = await GetAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("status-pill-paused", "the paused state is named, not left to look like a leaf nobody started");
		body.Should().NotContain("Finish 0 sessions and complete job", "a paused leaf has no sessions left to finish");
		var section = body[body.IndexOf("id=\"end-session\"", StringComparison.Ordinal)..];
		section = section[..section.IndexOf("</form>", StringComparison.Ordinal)];
		section.Should().Contain("<textarea id=\"writeUp\"", "the paused leaf's completion carries its write-up too");
		section.Should().Contain("Complete job");
		section.Should().Contain("Save write-up");
		section.Should().NotContain("Pause job", "there is no session left to pause");
	}

	[Fact]
	public async Task A_paused_leaf_can_be_completed_with_no_remaining_sessions()
	{
		var workerId = await SeedEmployeeAsync("work.paused-complete", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Paused then completed");
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
		var authCookie = await SignInAsync("work.paused-complete");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [], nodeVersion: leaf.Version, writeUp: "Wrapped up after the pause.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job completed.");
		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id });
		current.Node.WriteUp.Should().Be("Wrapped up after the pause.");
	}

	[Fact]
	public async Task Completing_a_job_saves_the_write_up_typed_beside_it()
	{
		var workerId = await SeedEmployeeAsync("work.complete-writeup", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Complete with write-up");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await SignInAsync("work.complete-writeup");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)],
			nodeVersion: leaf.Version, writeUp: "Ran long, but the fit is sound.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id });
		current.Node.WriteUp.Should().Be("Ran long, but the fit is sound.");
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id }, CancellationToken.None);
		leafWork.Achievement.Should().Be(Achievement.Success);
	}

	[Fact]
	public async Task Pausing_a_session_saves_the_write_up_typed_beside_it()
	{
		var workerId = await SeedEmployeeAsync("work.pause-writeup", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Pause with write-up");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await SignInAsync("work.pause-writeup");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostFinishAsync(
			authCookie, cookie, token, leaf.Id, workerId, session.Id.Value, session.Version,
			nodeVersion: leaf.Version, writeUp: "Stopping here; awaiting the replacement part.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id });
		current.Node.WriteUp.Should().Be("Stopping here; awaiting the replacement part.");
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task A_controlling_worker_can_save_the_leafs_write_up_without_affecting_other_fields()
	{
		var workerId = await SeedEmployeeAsync("work.writeup-save", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Write-up save leaf");
		var authCookie = await SignInAsync("work.writeup-save");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=SaveWriteUp");
		request.Headers.Add("Cookie", $"{authCookie}; {cookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leaf.Id.Value.ToString(CultureInfo.InvariantCulture),
			["nodeVersion"] = leaf.Version.ToString(CultureInfo.InvariantCulture),
			["writeUp"] = "Finished the trim work; used oak instead of pine per client request.",
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Write-up saved.");
		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id });
		current.Node.WriteUp.Should().Be("Finished the trim work; used oak instead of pine per client request.");
		current.Node.Description.Should().Be(leaf.Description);
		current.Node.OwnerUserId.Should().Be(workerId);
	}

	private async Task<HttpResponseMessage> PostCompleteAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, long leafWorkVersion,
		IReadOnlyList<(long SessionId, long Version)> sessions, string? finishedAt = null, string? completionNote = null,
		Achievement? finalAchievement = null, long? nodeVersion = null, string? writeUp = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Complete");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var pairs = new List<KeyValuePair<string, string>> {
			new("LeafNodeId", leafNodeId.Value.ToString(CultureInfo.InvariantCulture)),
			new("leafWorkVersion", leafWorkVersion.ToString(CultureInfo.InvariantCulture)),
			new("__RequestVerificationToken", token),
		};
		foreach (var (sessionId, version) in sessions) {
			pairs.Add(new("completeSessionId", sessionId.ToString(CultureInfo.InvariantCulture)));
			pairs.Add(new("completeSessionVersion", version.ToString(CultureInfo.InvariantCulture)));
		}

		if (finishedAt is not null) {
			pairs.Add(new("completionFinishedAt", finishedAt));
		}

		if (completionNote is not null) {
			pairs.Add(new("completionNote", completionNote));
		}

		if (finalAchievement is Achievement achievement) {
			pairs.Add(new("finalAchievement", achievement.ToString()));
		}

		if (nodeVersion is long nodeVersionValue) {
			pairs.Add(new("nodeVersion", nodeVersionValue.ToString(CultureInfo.InvariantCulture)));
		}

		if (writeUp is not null) {
			pairs.Add(new("writeUp", writeUp));
		}

		request.Content = new FormUrlEncodedContent(pairs);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostReopenAndStartAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, long leafWorkVersion, string reason,
		AppUserId workedByUserId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=ReopenAndStart");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["leafWorkVersion"] = leafWorkVersion.ToString(CultureInfo.InvariantCulture),
			["reason"] = reason,
			["reopenWorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	/// <summary>
	///     Posts the write-up's own standalone Save button -- the request site.js fires ahead of any
	///     other action form whenever a #writeUp textarea is on the page, since those handlers carry no
	///     write-up fields of their own (the one-handler-one-mutation architecture rule).
	/// </summary>
	private async Task<HttpResponseMessage> PostSaveWriteUpAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, long nodeVersion, string writeUp)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=SaveWriteUp");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["nodeVersion"] = nodeVersion.ToString(CultureInfo.InvariantCulture),
			["writeUp"] = writeUp,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<JobNodeResult> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description) =>
		await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

	private async Task<JobNodeResult> AddWorkedLeafAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var leaf = await AddChildAsync(parentId, ownerId, description);
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});

		return leaf;
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
	///     (<see cref="SeedEmployeeAsync" />), so a UTC-based wall time round-trips
	///     regardless of what zone the test process itself happens to run in.
	/// </summary>
	private static DateTimeOffset MinutesAgo(int minutes)
	{
		var now = DateTimeOffset.UtcNow;

		return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(-minutes);
	}

	private static string FormatForDateTimeLocal(DateTimeOffset value) => value.ToString(DateTimeLocalFormat, CultureInfo.InvariantCulture);

	private async Task<EquatableArray<WorkSessionResult>> GetSessionsAsync(JobNodeId leafId) =>
		await seedClient.Query.GetLeafSessionsAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, LeafWorkId = leafId },
			CancellationToken.None);

	private async Task<HttpResponseMessage> PostAsync(
		string handler, string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, AppUserId workedByUserId,
		string? startedAt = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Jobs/Work?handler={handler}");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (startedAt is not null) {
			fields["startedAt"] = startedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostFinishAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId leafNodeId, AppUserId workedByUserId, long sessionId, long version, string? finishedAt = null,
		long? nodeVersion = null, string? writeUp = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Finish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["sessionId"] = sessionId.ToString(CultureInfo.InvariantCulture),
			["version"] = version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (finishedAt is not null) {
			fields["finishedAt"] = finishedAt;
		}

		if (nodeVersion is long nodeVersionValue) {
			fields["nodeVersion"] = nodeVersionValue.ToString(CultureInfo.InvariantCulture);
		}

		if (writeUp is not null) {
			fields["writeUp"] = writeUp;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostCorrectAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId leafNodeId, AppUserId workedByUserId, WorkSessionId sessionId,
		string startedAt, string finishedAt, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/CorrectSession");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["SessionId"] = sessionId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.StartedAt"] = startedAt,
			["Input.FinishedAt"] = finishedAt,
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostClearFinishAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId leafNodeId, AppUserId workedByUserId, WorkSessionId sessionId,
		string startedAt, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/CorrectSession?handler=ClearFinish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["SessionId"] = sessionId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.StartedAt"] = startedAt,
			// A finished time is still posted (the field is populated); ClearFinish must ignore it.
			["Input.FinishedAt"] = "2026-01-01T17:00",
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetWorkFormAsync(string authCookie, JobNodeId leafNodeId, AppUserId workedByUserId) =>
		await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leafNodeId.Value}&workedByUserId={workedByUserId.Value}");

	private async Task<(string CookieHeader, string Token)> GetCorrectFormAsync(
		string authCookie, JobNodeId leafNodeId, AppUserId workedByUserId, WorkSessionId sessionId) =>
		await GetFormAsync(authCookie,
			$"/Jobs/CorrectSession?leafNodeId={leafNodeId.Value}&workedByUserId={workedByUserId.Value}&sessionId={sessionId.Value}");

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

	private static (long SessionId, long Version) ExtractFirstSession(string body)
	{
		var sessionIdMatch = SessionIdPattern().Match(body);
		var versionMatch = VersionPattern().Match(body);
		if (!sessionIdMatch.Success || !versionMatch.Success) {
			throw new InvalidOperationException("No session row found in Work page body.");
		}

		return (long.Parse(sessionIdMatch.Groups["id"].Value, CultureInfo.InvariantCulture),
			long.Parse(versionMatch.Groups["version"].Value, CultureInfo.InvariantCulture));
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

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

	[GeneratedRegex("name=\"sessionId\" value=\"(?<id>[0-9]+)\"")]
	private static partial Regex SessionIdPattern();

	[GeneratedRegex("name=\"version\" value=\"(?<version>[0-9]+)\"")]
	private static partial Regex VersionPattern();

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
