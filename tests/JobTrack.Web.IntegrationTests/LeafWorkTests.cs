namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Schedules;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
	public async Task A_worker_can_start_work_on_a_fresh_leaf_in_one_click()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.starter");
		var leaf = await AddChildAsync(rootId, workerId, "Pour foundation");
		var authCookie = await client.SignInAsync("work.starter");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		startBody.Should().Contain("Session started");
		startBody.Should().Contain("Active");
	}

	[Fact]
	/// <summary>
	/// The leaf's own Sessions table (_LeafWorkSessions, shared with Browse's embedded panel) prices
	/// each session on /Jobs/Work using the same &#163;figure / hours format as every other cost figure
	/// in the app (CostDisplay.FormatCell, jt-col-cost).
	/// </summary>
	public async Task The_work_page_sessions_table_shows_each_sessions_cost_in_the_shared_cost_and_hours_format()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.sessioncost");
		// Administrator, not CostViewer: /Jobs/Work's JobWorkflow policy admits only
		// Administrator/JobManager/Worker, unlike CostAccessPolicy.CanView's own Administrator-or-
		// CostViewer-or-ownership rule -- Administrator is the one role both admit.
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.sessioncost-viewer", EmployeeRole.Administrator);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Costed work-page session leaf");

		var sessionStart = Instant.FromUtc(2026, 1, 1, 9, 0);
		var sessionFinish = Instant.FromUtc(2026, 1, 1, 11, 0);
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Entry = new(ScheduleExceptionEffect.AddWorkingTime, new(sessionStart, sessionFinish.Plus(Duration.FromHours(1))), null),
			Reason = "Full working window for work-page session-cost test",
		});
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Rate = new(new(60m), Instant.FromUtc(2000, 1, 1, 0, 0), null),
		});
		var session = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.CorrectSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = session.Id,
			StartedAt = sessionStart,
			FinishedAt = sessionFinish,
			Reason = "Pin to a deterministic instant for work-page session-cost test",
			Version = session.Version,
		});

		var authCookie = await client.SignInAsync("work.sessioncost-viewer");
		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("<th class=\"jt-col-cost col-md-3 col-lg-3 text-end d-none d-md-table-cell\">Cost</th>");
		body.Should().Contain("&#xA3;120.00");
		body.Should().Contain("2.0 hrs");
	}

	[Fact]
	public async Task Starting_a_session_leaves_the_standalone_write_up_unchanged()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.start-writeup");
		var leaf = await AddChildAsync(rootId, workerId, "Pour foundation with write-up");
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
		});
		var authCookie = await client.SignInAsync("work.start-writeup");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			});
		current.Node.WriteUp.Should().BeNull();
	}

	/// <summary>
	///     Reopening a successful prerequisite is still permitted (ADR 0044 rule 6), so the page has to
	///     say when a dependent's session is running right now — the composite reopen refuses outright
	///     in that case, and this notice is the only warning the elevated session-free reopen gets.
	/// </summary>
	[Fact]
	public async Task The_work_page_warns_when_a_dependent_is_working_right_now_on_a_successful_leaf()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.dependent-warning");
		var required = await AddWorkedLeafAsync(rootId, workerId, "Site survey");
		var dependent = await AddChildAsync(rootId, workerId, "Excavate foundations");
		var context = new CommandContext {
			Actor = administratorId,
			CorrelationId = Guid.NewGuid(),
		};
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = context,
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
		var inProgress = await seedClient.Work.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = required.Id,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = 1,
		});
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = required.Id,
			NewAchievement = Achievement.Success,
			Reason = "Surveyed",
			Version = inProgress.Version,
		});
		var authCookie = await client.SignInAsync("work.dependent-warning");

		var beforeDependentStarts = await (await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={required.Id.Value}", authCookie))
										  .Content.ReadAsStringAsync();
		// The count sentence is wrapped across source lines by Razor, so match its stable fragments.
		beforeDependentStarts.Should().Contain("1 job", "the dependent-count warning is unconditional");
		beforeDependentStarts.Should().Contain("blocked again");
		beforeDependentStarts.Should().NotContain("work in progress right now");

		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = context,
			JobNodeId = dependent.Id,
			WorkedByUserId = workerId,
		});

		var whileDependentWorks = await (await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={required.Id.Value}", authCookie))
										.Content.ReadAsStringAsync();
		whileDependentWorks.Should().Contain("work in progress right now");
	}

	[Fact]
	public async Task Starting_work_on_the_root_shows_a_helpful_error()
	{
		var jobManagerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.root-error", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("work.root-error");

		var (cookie, token) = await GetWorkFormAsync(authCookie, rootId, jobManagerId);
		var response = await PostAsync("Start", authCookie, cookie, token, rootId, jobManagerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("cannot hold LeafWork");
	}

	[Fact]
	public async Task A_worker_can_finish_their_own_active_session()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.finisher");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Frame walls");
		var authCookie = await client.SignInAsync("work.finisher");

		var (startCookie, startToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, startCookie, startToken, leaf.Id, workerId);
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		var (sessionId, version) = ExtractFirstSession(startBody);

		var (finishCookie, finishToken) = await WebTestHttp.ExtractFormAsync(startReloaded, startCookie);
		var finishResponse = await PostFinishAsync(authCookie, finishCookie, finishToken, leaf.Id, workerId, sessionId, version);
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await client.FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();
		finishBody.Should().Contain("Ends this session; the job stays In Progress.");
	}

	[Fact]
	/// <summary>
	/// Below lg, the Sessions table's own Started column is hidden, so its "Finished" heading relabels
	/// to "Status" there and an open session's status cell carries the compact start time inside a
	/// same-size --compact status-pill-active pill instead of the plain word "Active", while a closed
	/// session's finish time gains its own --compact status-pill-closed pill -- distinct colours, equal
	/// size, per design-language.md's "stop and go" pill vocabulary.
	/// </summary>
	public async Task The_sessions_table_marks_open_and_closed_sessions_with_distinct_narrow_screen_pills()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.narrowpills");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Narrow screen pills");
		var authCookie = await client.SignInAsync("work.narrowpills");

		var (startCookie, startToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, startCookie, startToken, leaf.Id, workerId);
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		var (sessionId, version) = ExtractFirstSession(startBody);

		startBody.Should().Contain(">Finished<");
		startBody.Should().Contain(">Status<");
		startBody.Should().Contain("status-pill-active status-pill--compact");
		startBody.Should().NotContain("status-pill-ready", "the app's green readiness colour is reserved for gates, not a running session");

		var (finishCookie, finishToken) = await WebTestHttp.ExtractFormAsync(startReloaded, startCookie);
		var finishResponse = await PostFinishAsync(authCookie, finishCookie, finishToken, leaf.Id, workerId, sessionId, version);
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await client.FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();

		finishBody.Should().Contain("status-pill-closed status-pill--compact");
	}

	[Fact]
	public async Task The_leaf_toolbar_shows_finish_instead_of_start_once_a_session_is_active()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.toggle");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Toggle toolbar leaf");
		var authCookie = await client.SignInAsync("work.toggle");

		var beforeResponse = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);
		var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
		beforeBody.Should().Contain("#jt-icon-start");
		beforeBody.Should().Contain("Start session");
		beforeBody.Should().NotContain("Start work");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		startBody.Should().Contain("Pause job");
		startBody.Should().NotContain("Finish / pause");
		// Plan §4.1: the viewer's own one-click Start is replaced by Pause job, but the
		// authorized "Start for..." disclosure (also drawn with jt-icon-start) for another worker is
		// never removed -- only the viewer's own primary action toggles.
		startBody.Should().NotContain("title=\"Start session\"");
	}

	[Fact]
	public async Task Both_start_disclosure_panels_give_their_submit_the_primary_button_treatment()
	{
		// A floating disclosure panel has exactly one action, and it is why the panel was opened, so
		// it takes `btn btn-primary` in both panels. "Start for…" used to hardcode `btn-secondary`
		// while the backdated-start panel beside it took the accent, which read as the two panels
		// disagreeing about whether their own submit was the thing to click.
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.panel.primary-owner", EmployeeRole.Administrator);
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.panel.primary-target");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Panel button leaf");
		var authCookie = await client.SignInAsync("work.panel.primary-owner");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={ownerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("Start for…", "the disclosure under test must actually be on the page");
		SubmitButtonClass(body, StartForSubmitPattern(), "Start session for worker").Should().Be("btn btn-primary");
		SubmitButtonClass(body, BackdatedStartSubmitPattern(), "Start session at this time").Should().Be("btn btn-primary");
	}

	/// <summary>The <c>class</c> of the submit button <paramref name="pattern" /> matches.</summary>
	private static string SubmitButtonClass(string body, Regex pattern, string label)
	{
		var match = pattern.Match(body);
		match.Success.Should().BeTrue($"the page should have a submit button labelled '{label}'");
		return match.Groups["class"].Value;
	}

	[Fact]
	public async Task The_start_for_disclosure_survives_an_active_session_on_the_leaf()
	{
		// Same rule as Browse's leaf toolbar: only the viewer's *own* primary action toggles when they
		// clock on. A leaf can have several simultaneous workers, so an open session must never remove
		// the authorized "Start for..." control -- otherwise a second worker cannot be started at all.
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.startfor.active-owner");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.startfor.active-target");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Concurrent start-for leaf");
		var authCookie = await client.SignInAsync("work.startfor.active-owner");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, ownerId);
		var startResponse = await PostAsync("Start", authCookie, cookie, token, leaf.Id, ownerId);
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var reloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Pause job");
		body.Should().Contain("Start for…");
		body.Should().Contain("name=\"StartForUserId\"");
	}

	[Fact]
	public async Task A_viewer_with_no_session_of_their_own_can_still_start_one_while_another_worker_is_active()
	{
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.startfor.second-owner");
		var otherWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.startfor.second-worker");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Second worker leaf");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = otherWorkerId,
		});
		var authCookie = await client.SignInAsync("work.startfor.second-owner");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		// The viewer's own one-click Start, not the "Start session for worker" button inside the
		// Start-for disclosure -- hence matching the button's whole text, not a substring of the page.
		OwnStartButtonPattern().IsMatch(body).Should().BeTrue();
	}

	[Fact]
	public async Task Pausing_a_job_returns_to_Browse_rooted_at_the_leaf()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-redirect");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Pause redirect leaf");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.pause-redirect");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostPauseAsync(authCookie, cookie, token, leaf.Id, [(session.Id.Value, session.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be($"/Jobs/Browse?nodeId={leaf.Id.Value}");
		var body = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().Contain("Ends this session; the job stays In Progress.");
	}

	[Fact]
	public async Task Pausing_a_job_ends_every_worker_s_session_not_just_the_actor_s()
	{
		// The whole point of Pause: a leaf two people are clocked onto is paused for both of them. Ending
		// only the actor's own session leaves the job reading as active with a colleague's clock running.
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-all-owner");
		var mateId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-all-mate");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Two-worker pause leaf");
		var ownSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = ownerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = ownerId,
		});
		var mateSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = ownerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = mateId,
		});
		var authCookie = await client.SignInAsync("work.pause-all-owner");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, ownerId);
		var response = await PostPauseAsync(
			authCookie, cookie, token, leaf.Id,
			[(ownSession.Id.Value, ownSession.Version), (mateSession.Id.Value, mateSession.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be($"/Jobs/Browse?nodeId={leaf.Id.Value}");
		var sessions = await GetSessionsAsync(leaf.Id);
		sessions.Should().HaveCount(2).And.OnlyContain(s => s.FinishedAt != null, "pausing a job stops every clock on it");
		sessions.Select(s => s.FinishedAt).Distinct().Should().ContainSingle("all of them stop at the one instant");
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				JobNodeId = leaf.Id,
			},
			CancellationToken.None);
		leafWork.Achievement.Should().Be(Achievement.InProgress, "a pause never closes the job");
	}

	[Fact]
	public async Task The_pause_button_confirms_every_active_session_on_the_page()
	{
		// The rendered form is what proves Pause can end both -- posting a hand-built set would test the
		// handler while leaving the page free to keep submitting only the viewer's own session.
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-form-owner");
		var mateId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-form-mate");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Pause form leaf");
		var ownSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = ownerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = ownerId,
		});
		var mateSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = ownerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = mateId,
		});
		var authCookie = await client.SignInAsync("work.pause-form-owner");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("handler=Pause").And.Contain("Pause job");
		body.Should().Contain($"name=\"endSessionId\" value=\"{ownSession.Id.Value}\"");
		body.Should().Contain($"name=\"endSessionId\" value=\"{mateSession.Id.Value}\"");
		body.Should().Contain("Ends all 2 active sessions; the job stays In Progress.");
	}

	[Fact]
	public async Task Pausing_with_a_session_set_that_moved_underneath_the_page_conflicts_rather_than_pausing_part_of_the_leaf()
	{
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-stale-owner");
		var mateId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-stale-mate");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Stale pause leaf");
		var ownSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = ownerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = ownerId,
		});
		var authCookie = await client.SignInAsync("work.pause-stale-owner");
		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, ownerId);
		// The mate clocks on between the page render and the post.
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = ownerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = mateId,
		});

		var response = await PostPauseAsync(authCookie, cookie, token, leaf.Id, [(ownSession.Id.Value, ownSession.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().StartWith("/Jobs/Work");
		var body = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().Contain("Someone else changed one of this leaf");
		(await GetSessionsAsync(leaf.Id)).Should().OnlyContain(
			s => s.FinishedAt == null, "a conflicted pause must leave every clock exactly as it was");
	}

	[Fact]
	public async Task Completing_a_job_returns_to_Browse_rooted_at_the_leaf()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-redirect");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Complete redirect leaf");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.complete-redirect");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be($"/Jobs/Browse?nodeId={leaf.Id.Value}");
		var body = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().Contain("Job completed and session finished.");
	}

	[Fact]
	public async Task Completing_several_sessions_at_once_returns_to_Browse_rooted_at_the_leaf()
	{
		// "Finish N sessions and complete job" is the same Complete button under a plural label, so it
		// lands in the same place -- proved with the plural set rather than assumed from the singular.
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-many-redirect");
		var mateId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-many-mate");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Two-worker redirect leaf");
		var ownSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var mateSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = mateId,
		});
		var authCookie = await client.SignInAsync("work.complete-many-redirect");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2,
			[(ownSession.Id.Value, ownSession.Version), (mateSession.Id.Value, mateSession.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be($"/Jobs/Browse?nodeId={leaf.Id.Value}");
		var body = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().Contain("Job completed and 2 sessions finished.");
	}

	[Fact]
	public async Task A_failed_pause_stays_on_the_work_page_rather_than_returning_to_Browse()
	{
		// Only the success path leaves; a rejected post has to redisplay its own page with the error.
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-failure-redirect");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Failed pause leaf");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.pause-failure-redirect");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostPauseAsync(
			authCookie, cookie, token, leaf.Id, [(session.Id.Value, session.Version)], "not-a-local-date-time");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().StartWith("/Jobs/Work");
		var body = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid date and time.");
	}

	[Fact]
	public async Task A_failed_completion_stays_on_the_work_page_rather_than_returning_to_Browse()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-failure-redirect");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Failed completion leaf");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.complete-failure-redirect");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)], "not-a-local-date-time");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().StartWith("/Jobs/Work");
		var body = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid completion date and time.");
	}

	[Fact]
	public async Task Starting_a_session_stays_on_the_work_page()
	{
		// Deliberately unlike Pause/Complete: starting work is the beginning of a stay on this page, so
		// it redisplays the leaf's own Sessions rather than bouncing to Browse.
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.start-stays");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Start stays leaf");
		var authCookie = await client.SignInAsync("work.start-stays");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().StartWith("/Jobs/Work");
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_time_from_the_leaf_toolbar()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.backdate-starter");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Backdated start leaf");
		var authCookie = await client.SignInAsync("work.backdate-starter");
		var backdatedAt = MinutesAgo(HoursBackdated * MinutesPerHour);

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId, FormatForDateTimeLocal(backdatedAt));

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");

		var sessions = await GetSessionsAsync(leaf.Id);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(Instant.FromDateTimeOffset(backdatedAt));
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_time_from_the_leaf_toolbar_shows_a_helpful_error()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.future-starter");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Future start leaf");
		var authCookie = await client.SignInAsync("work.future-starter");
		var future = FormatForDateTimeLocal(MinutesAgo(-HoursBackdated * MinutesPerHour));

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId, future);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("in the future");
	}

	[Fact]
	public async Task Starting_a_session_with_a_malformed_backdate_from_the_leaf_toolbar_does_not_start_work()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.malformed-starter");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Malformed start leaf");
		var authCookie = await client.SignInAsync("work.malformed-starter");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId, "not-a-local-date-time");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid date and time.");
		(await GetSessionsAsync(leaf.Id)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_worker_can_finish_a_session_with_a_backdated_time_from_the_sessions_panel()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.backdate-finisher");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Backdated finish leaf");
		var authCookie = await client.SignInAsync("work.backdate-finisher");
		var startedAt = MinutesAgo(HoursBeforeFinish * MinutesPerHour);
		var finishedAt = MinutesAgo(HoursBeforeNowFinished * MinutesPerHour);

		var (startCookie, startToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, startCookie, startToken, leaf.Id, workerId, FormatForDateTimeLocal(startedAt));
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		var (sessionId, version) = ExtractFirstSession(startBody);

		var (finishCookie, finishToken) = await WebTestHttp.ExtractFormAsync(startReloaded, startCookie);
		var finishResponse = await PostFinishAsync(
			authCookie, finishCookie, finishToken, leaf.Id, workerId, sessionId, version, FormatForDateTimeLocal(finishedAt));
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await client.FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();
		finishBody.Should().Contain("Ends this session; the job stays In Progress.");

		var sessions = await GetSessionsAsync(leaf.Id);
		sessions.Should().ContainSingle().Which.FinishedAt.Should().Be(Instant.FromDateTimeOffset(finishedAt));
	}

	[Fact]
	public async Task The_sessions_panel_offers_pause_as_an_icon_and_uses_the_explicit_outcome_label()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.finish-icon");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Finish icon session leaf");
		var authCookie = await client.SignInAsync("work.finish-icon");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("#jt-icon-finish");
		// Both the icon-only row action and the page toolbar name the outcome consistently.
		body.Should().Contain("Pause job");
		body.Should().NotContain("Finish / pause");
	}

	[Fact]
	public async Task The_leaf_toolbar_offers_a_backdate_disclosure_beside_start()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.backdate-disclosure");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Backdate disclosure leaf");
		var authCookie = await client.SignInAsync("work.backdate-disclosure");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);
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
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.owner");
		var otherWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.snooper");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Private work");
		var authCookie = await client.SignInAsync("work.snooper");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("may not view");
		_ = otherWorkerId;
	}

	[Fact]
	public async Task An_administrator_can_correct_a_workers_historical_session_with_a_reason()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.correctable");
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.correcting-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Correctable work");

		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
		});

		var authCookie = await client.SignInAsync("work.correcting-manager");

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
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.default-all.owner");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Default-all leaf");
		var authCookie = await client.SignInAsync("work.default-all.owner");

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
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.default-self.owner");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.default-self.viewer");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Default-self leaf");
		var authCookie = await client.SignInAsync("work.default-self.viewer");

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
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.filtermem.owner");
		var otherWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.filtermem.other");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Filter memory leaf");
		var authCookie = await client.SignInAsync("work.filtermem.owner");

		// Explicitly filter to the other worker; capture the session that now remembers the choice.
		using var chooseRequest = new HttpRequestMessage(
			HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}&WorkedByUserId={otherWorkerId.Value}");
		chooseRequest.Headers.Add("Cookie", authCookie);
		var chooseResponse = await client.SendAsync(chooseRequest);
		var sessionCookie = WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(chooseResponse, "JobTrack.Filters") ?? throw new InvalidOperationException("No session cookie was set."));

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
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.clearfinish");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.clearfinish-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Clear finish work");
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
		});
		var authCookie = await client.SignInAsync("work.clearfinish-manager");

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
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.clearfinish-noreason");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.clearfinish-noreason-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Clear finish no reason work");
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
		});
		var authCookie = await client.SignInAsync("work.clearfinish-noreason-manager");

		var (cookie, token) = await GetCorrectFormAsync(authCookie, leaf.Id, workerId, finished.Id);
		var response = await PostClearFinishAsync(
			authCookie, cookie, token, leaf.Id, workerId, finished.Id, "2026-01-01T09:00", string.Empty);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Correcting_a_session_with_a_malformed_optional_finish_does_not_reopen_it()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.malformed-correction");
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.malformed-correcting-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Malformed correction work");
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var finished = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
		});
		var authCookie = await client.SignInAsync("work.malformed-correcting-manager");

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
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.unknown-zone");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Unknown zone leaf");
		await using (var connection = new SqliteConnection(database.ConnectionString)) {
			await connection.OpenAsync();
			await using var command = connection.CreateCommand();
			command.CommandText = "UPDATE app_user SET iana_time_zone = 'Etc/No_Such_Zone' WHERE id = $id;";
			_ = command.Parameters.AddWithValue("$id", workerId.Value);
			_ = await command.ExecuteNonQueryAsync();
		}

		var authCookie = await client.SignInAsync("work.unknown-zone");
		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}&workedByUserId={workerId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
	}

	[Fact]
	public async Task A_controlling_worker_can_finish_sessions_and_record_a_non_success_outcome_via_the_completion_dropdown()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-cancelled");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Site access withdrawn");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.complete-cancelled");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)], finalAchievement: Achievement.Cancelled);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job cancelled and session finished.");
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				JobNodeId = leaf.Id,
			}, CancellationToken.None);
		leafWork.Achievement.Should().Be(Achievement.Cancelled);
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task A_controlling_worker_can_complete_a_job_with_one_active_session_from_the_work_page()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-one");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.complete-one");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job completed and session finished.");
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				JobNodeId = leaf.Id,
			}, CancellationToken.None);
		leafWork.Achievement.Should().Be(Achievement.Success);
	}

	[Fact]
	public async Task Completion_options_backdate_the_active_set_and_persist_the_optional_note()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-options");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Complete with options");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
			StartedAt = Instant.FromDateTimeOffset(MinutesAgo(HoursBeforeFinish * MinutesPerHour)),
		});
		var finishedAt = MinutesAgo(HoursBeforeNowFinished * MinutesPerHour);
		var authCookie = await client.SignInAsync("work.complete-options");

		var page = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var pageBody = await page.Content.ReadAsStringAsync();
		pageBody.Should().Contain("Completion options");
		pageBody.Should().Contain("name=\"completionFinishedAt\"");
		pageBody.Should().Contain("name=\"completionNote\"");
		var (cookie, token) = await WebTestHttp.ExtractFormAsync(page, WebTestHttp.FindSetCookie(page, "Antiforgery") is string setCookie
			? WebTestHttp.ExtractCookiePair(setCookie)
			: throw new InvalidOperationException("No antiforgery cookie in Work response."));
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)],
			FormatForDateTimeLocal(finishedAt), "Customer confirmed acceptance");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().Be(Instant.FromDateTimeOffset(finishedAt));
		var audit = await seedClient.Audit.SearchAuditEventsAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			Filter = new() {
				EntityType = "leaf_work",
				EntityId = leaf.Id.Value,
			},
		});
		audit.Events.Should().Contain(entry => entry.Reason == "Completed from the leaf work page (Customer confirmed acceptance)");
	}

	[Fact]
	public async Task Completing_a_job_with_two_active_sessions_finishes_both_and_records_success()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-two");
		var otherWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-two-other");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets, two workers");
		var first = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var second = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = otherWorkerId,
		});
		var authCookie = await client.SignInAsync("work.complete-two");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(first.Id.Value, first.Version), (second.Id.Value, second.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job completed and 2 sessions finished.");
		(await GetSessionsAsync(leaf.Id)).Should().OnlyContain(s => s.FinishedAt != null);
	}

	[Fact]
	public async Task Several_active_sessions_show_an_always_expanded_completion_review_without_repeating_the_sessions_list()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.review-manager", EmployeeRole.JobManager);
		var firstWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.review-first");
		var secondWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.review-second");
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Review several sessions");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = managerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = firstWorkerId,
		});
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = managerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = secondWorkerId,
		});
		var authCookie = await client.SignInAsync("work.review-manager");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
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
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, $"work.outcome-{achievement}", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, $"{achievement} leaf");
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = managerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			NewAchievement = achievement,
			Reason = "Closing for copy test",
			Version = 1,
		});
		var authCookie = await client.SignInAsync($"work.outcome-{achievement}");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain(expectedCopy);
		body.Should().NotContain("<p>Completed. To record more work");
		body.Should().Contain($"selected=\"selected\" value=\"{managerId.Value}\"", "the reopen target must default to the viewer");
	}

	[Fact]
	public async Task A_non_controlling_worker_cannot_complete_a_job_from_the_work_page()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-forbidden-owner");
		var strangerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-forbidden-stranger");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Fit cabinets");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.complete-forbidden-stranger");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, strangerId);
		var response = await PostCompleteAsync(authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)]);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_non_controlling_worker_sees_no_doomed_start_or_outcome_controls()
	{
		var ownerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.capabilities-owner");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.capabilities-bystander");
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Capability-gated leaf");
		var authCookie = await client.SignInAsync("work.capabilities-bystander");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain(">Start session</button>");
		body.Should().NotContain(">Cancel job</button>");
		body.Should().NotContain(">Mark unsuccessful</button>");
		body.Should().Contain("A controlling owner, Job Manager, or Administrator can start work on this job.");
	}

}
