namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Schedules;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Direct-HTTP tests for personal schedule and exception management (plan §8.5 slice 6, spec
///     §8.1/§8.3): adding a schedule version and a schedule exception, and the self-or-administrator
///     visibility/authorization rule.
/// </summary>
public sealed partial class ScheduleTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		_ = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.schedule-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});

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
	public async Task A_worker_can_add_their_own_schedule_version_and_exception()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.worker");
		var authCookie = await client.SignInAsync("schedule.worker");

		var (versionCookie, versionToken) = await GetFormAsync(authCookie, workerId);
		var versionResponse = await PostAddVersionAsync(authCookie, versionCookie, versionToken, workerId, "2026-01-01", "Europe/London");
		versionResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var versionReloaded = await client.FollowRedirectAsync(versionResponse, authCookie);
		var versionBody = await versionReloaded.Content.ReadAsStringAsync();

		versionBody.Should().Contain("Rota version added");
		versionBody.Should().Contain("Europe/London");
		versionBody.Should().Contain("<td class=\"col-10 col-md-5 col-lg-3\">Thursday, 1 January 2026</td>");

		var (exceptionCookie, exceptionToken) = await WebTestHttp.ExtractFormAsync(versionReloaded, versionCookie);
		var exceptionResponse = await PostAddExceptionAsync(
			authCookie, exceptionCookie, exceptionToken, workerId, "RemoveWorkingTime",
			"2026-01-05T00:00", "2026-01-06T00:00", "Public holiday");
		exceptionResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var exceptionReloaded = await client.FollowRedirectAsync(exceptionResponse, authCookie);
		var exceptionBody = await exceptionReloaded.Content.ReadAsStringAsync();

		exceptionBody.Should().Contain("Rota exception added");
		exceptionBody.Should().Contain("Public holiday");
	}

	[Fact]
	public async Task An_unparsable_exception_start_or_end_shows_an_error_bubble_instead_of_being_silently_swallowed()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.bad-datetime");
		var authCookie = await client.SignInAsync("schedule.bad-datetime");

		var (versionCookie, versionToken) = await GetFormAsync(authCookie, workerId);
		var versionResponse = await PostAddVersionAsync(authCookie, versionCookie, versionToken, workerId, "2026-01-01", "Europe/London");
		var versionReloaded = await client.FollowRedirectAsync(versionResponse, authCookie);

		var (exceptionCookie, exceptionToken) = await WebTestHttp.ExtractFormAsync(versionReloaded, versionCookie);
		var response = await PostAddExceptionAsync(
			authCookie, exceptionCookie, exceptionToken, workerId, "RemoveWorkingTime",
			"not-a-date", "2026-01-06T00:00", "Public holiday");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK, "an invalid date/time re-renders the page inline rather than redirecting");
		body.Should().Contain("alert-danger", "the error must render through the same bubble every other page uses, not be silently dropped");
		body.Should().Contain("Start and end must each be a valid date and time.");
	}

	[Fact]
	public async Task The_schedule_page_defaults_the_effective_start_to_today_and_uses_human_friendly_field_labels()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.labels");
		var authCookie = await client.SignInAsync("schedule.labels");

		var response = await client.GetAuthenticatedAsync($"/Rota/Index?userId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(
			$"id=\"VersionInput_EffectiveStart\" name=\"VersionInput.EffectiveStart\" value=\"{DateOnly.FromDateTime(DateTime.Today):yyyy-MM-dd}\"");
		body.Should().Contain(">Effective start</label>");
		body.Should().Contain(">Effective end (leave blank if still current)</label>");
		body.Should().Contain(">IANA time zone</label>");
		body.Should().Contain(">Day</label>");
		body.Should().Contain(">Start</label>");
		body.Should().Contain(">End</label>");
		body.Should().Contain(">Effect</label>");
		body.Should().Contain(">Reason</label>");
		body.Should().NotContain(">EffectiveStart</label>");
		body.Should().NotContain(">IanaTimeZone</label>");
	}

	[Fact]
	public async Task A_worker_cannot_add_a_schedule_version_for_another_employee()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.self");
		var otherWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.other");
		var authCookie = await client.SignInAsync("schedule.self");

		var (cookie, token) = await GetFormAsync(authCookie, otherWorkerId);
		var response = await PostAddVersionAsync(authCookie, cookie, token, otherWorkerId, "2026-01-01", "Europe/London");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task Adding_a_schedule_version_with_an_unrecognized_zone_id_returns_the_page_with_validation()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.bad-zone");
		var authCookie = await client.SignInAsync("schedule.bad-zone");

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddVersionAsync(authCookie, cookie, token, workerId, "2026-01-01", "Bogus/NotAZone");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("That is not a recognized IANA time zone.");
	}

	[Fact]
	public async Task A_worker_cannot_view_another_employees_schedule()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.viewer");
		var otherWorkerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.viewed");
		var authCookie = await client.SignInAsync("schedule.viewer");

		var response = await client.GetAuthenticatedAsync($"/Rota/Index?userId={otherWorkerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("may not view");
	}

	[Fact]
	public async Task An_administrator_can_add_a_schedule_version_for_another_employee()
	{
		var adminUserId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.admin", EmployeeRole.Administrator);
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.target");
		var authCookie = await client.SignInAsync("schedule.admin");

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddVersionAsync(authCookie, cookie, token, workerId, "2026-01-01", "Europe/London");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Rota version added");
	}

	[Fact]
	public async Task A_cost_viewer_cannot_open_schedule_administration()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.target-denied");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.viewer-denied", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("schedule.viewer-denied");

		var response = await client.GetAuthenticatedAsync($"/Rota/Index?userId={workerId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_version()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.correct-version");
		var added = await seedClient.Schedules.AddScheduleVersionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"], new(2026, 1, 1), null,
				[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]),
		});
		var authCookie = await client.SignInAsync("schedule.correct-version");

		var (cookie, token) = await GetFormAsync(authCookie, $"/Rota/CorrectVersion?userId={workerId.Value}&versionId={added.Id.Value}");
		var response = await PostCorrectVersionAsync(
			authCookie, cookie, token, workerId, added.Id, "2026-02-01", "Europe/London", "Fixed a typo in the start date");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Rota");
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_exception()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.correct-exception");
		var added = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
				null),
			Reason = "Public holiday",
		});
		var authCookie = await client.SignInAsync("schedule.correct-exception");

		var (cookie, token) = await GetFormAsync(authCookie, $"/Rota/CorrectException?userId={workerId.Value}&exceptionId={added.Id.Value}");
		var response = await PostCorrectExceptionAsync(
			authCookie, cookie, token, workerId, added.Id,
			"RemoveWorkingTime", "2026-01-03T00:00", "2026-01-04T00:00", "Wrong date entered originally");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Rota");
	}

	[Fact]
	// Auckland (NZST, UTC+12 in June, no DST) never coincides with whatever zone the test process's
	// own machine happens to run in, so a round trip through this employee's own zone proves the
	// exception boundary is genuinely zone-converted, not passed through as UTC.
	public async Task A_worker_can_add_a_schedule_exception_in_their_own_non_uk_zone()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "schedule.tz-auckland", EmployeeRole.Worker, "Pacific/Auckland");
		var authCookie = await client.SignInAsync("schedule.tz-auckland");

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddExceptionAsync(
			authCookie, cookie, token, workerId, "RemoveWorkingTime", "2026-06-15T09:00", "2026-06-15T17:00", "Public holiday");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var snapshot = await seedClient.Query.GetScheduleAsync(
			new() {
				Context = new() {
					Actor = workerId,
					CorrelationId = Guid.NewGuid(),
				},
				UserId = workerId,
			});

		var interval = snapshot.Exceptions.Should().ContainSingle().Which.Entry.Interval;
		interval.Start.Should().Be(Instant.FromUtc(2026, 6, 14, 21, 0), "09:00 NZST (UTC+12) on 15 June is 21:00 UTC the day before");
		interval.End.Should().Be(Instant.FromUtc(2026, 6, 15, 5, 0), "17:00 NZST (UTC+12) on 15 June is 05:00 UTC the same day");
	}

	private async Task<HttpResponseMessage> PostCorrectVersionAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId, ScheduleVersionId versionId,
		string effectiveStart, string ianaTimeZone, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Rota/CorrectVersion");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["VersionId"] = versionId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.EffectiveStart"] = effectiveStart,
			["Input.IanaTimeZone"] = ianaTimeZone,
			["Input.WeeklyIntervals[0].Day"] = "Monday",
			["Input.WeeklyIntervals[0].Start"] = "09:00",
			["Input.WeeklyIntervals[0].End"] = "17:00",
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostCorrectExceptionAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId, ScheduleExceptionId exceptionId,
		string effect, string start, string end, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Rota/CorrectException");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["ExceptionId"] = exceptionId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.Effect"] = effect,
			["Input.Start"] = start,
			["Input.End"] = end,
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostAddVersionAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId, string effectiveStart, string ianaTimeZone)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Rota/Index?handler=AddVersion");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["VersionInput.EffectiveStart"] = effectiveStart,
			["VersionInput.IanaTimeZone"] = ianaTimeZone,
			["VersionInput.WeeklyIntervals[0].Day"] = "Monday",
			["VersionInput.WeeklyIntervals[0].Start"] = "09:00",
			["VersionInput.WeeklyIntervals[0].End"] = "17:00",
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostAddExceptionAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId,
		string effect, string start, string end, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Rota/Index?handler=AddException");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["ExceptionInput.Effect"] = effect,
			["ExceptionInput.Start"] = start,
			["ExceptionInput.End"] = end,
			["ExceptionInput.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, AppUserId userId) =>
		await GetFormAsync(authCookie, $"/Rota/Index?userId={userId.Value}");

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Schedule page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Schedule page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}





	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();
}
