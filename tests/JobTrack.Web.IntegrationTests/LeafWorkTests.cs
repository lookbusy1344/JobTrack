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
		startBody.Should().Contain("Work started");
		startBody.Should().Contain("Active");
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
		finishBody.Should().Contain("Session finished");
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

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var startResponse = await PostAsync("Start", authCookie, cookie, token, leaf.Id, workerId);

		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var startReloaded = await FollowRedirectAsync(startResponse, authCookie);
		var startBody = await startReloaded.Content.ReadAsStringAsync();
		startBody.Should().Contain("Finish work");
		startBody.Should().NotContain("#jt-icon-start");
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
		body.Should().Contain("Work started");

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
		finishBody.Should().Contain("Session finished");

		var sessions = await GetSessionsAsync(leaf.Id);
		sessions.Should().ContainSingle().Which.FinishedAt.Should().Be(Instant.FromDateTimeOffset(finishedAt));
	}

	[Fact]
	public async Task The_sessions_panel_offers_finish_as_an_icon_and_the_toolbar_keeps_its_label()
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
		// The panel's row action is icon-only; the page toolbar above it keeps its label.
		body.Should().Contain("Finish work");
		body.Should().NotContain("btn btn-secondary\">Finish / pause");
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
		correctResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Work");
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
		JobNodeId leafNodeId, AppUserId workedByUserId, long sessionId, long version, string? finishedAt = null)
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
