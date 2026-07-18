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
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

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
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		_ = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.schedule-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});

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
	public async Task A_worker_can_add_their_own_schedule_version_and_exception()
	{
		var workerId = await SeedEmployeeAsync("schedule.worker", EmployeeRole.Worker);
		var authCookie = await SignInAsync("schedule.worker");

		var (versionCookie, versionToken) = await GetFormAsync(authCookie, workerId);
		var versionResponse = await PostAddVersionAsync(authCookie, versionCookie, versionToken, workerId, "2026-01-01", "Europe/London");
		var versionBody = await versionResponse.Content.ReadAsStringAsync();

		versionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		versionBody.Should().Contain("Rota version added");
		versionBody.Should().Contain("Europe/London");
		versionBody.Should().Contain("<td>Thursday, 1 January 2026</td>");

		var (exceptionCookie, exceptionToken) = await ExtractFormAsync(versionResponse, versionCookie);
		var exceptionResponse = await PostAddExceptionAsync(
			authCookie, exceptionCookie, exceptionToken, workerId, "RemoveWorkingTime",
			"2026-01-05T00:00:00+00:00", "2026-01-06T00:00:00+00:00", "Public holiday");
		var exceptionBody = await exceptionResponse.Content.ReadAsStringAsync();

		exceptionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		exceptionBody.Should().Contain("Rota exception added");
		exceptionBody.Should().Contain("Public holiday");
	}

	[Fact]
	public async Task The_schedule_page_defaults_the_effective_start_to_today_and_uses_human_friendly_field_labels()
	{
		var workerId = await SeedEmployeeAsync("schedule.labels", EmployeeRole.Worker);
		var authCookie = await SignInAsync("schedule.labels");

		var response = await GetAsync($"/Rota/Index?userId={workerId.Value}", authCookie);
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
		var workerId = await SeedEmployeeAsync("schedule.self", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("schedule.other", EmployeeRole.Worker);
		var authCookie = await SignInAsync("schedule.self");

		var (cookie, token) = await GetFormAsync(authCookie, otherWorkerId);
		var response = await PostAddVersionAsync(authCookie, cookie, token, otherWorkerId, "2026-01-01", "Europe/London");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task Adding_a_schedule_version_with_an_unrecognized_zone_id_returns_the_page_with_validation()
	{
		var workerId = await SeedEmployeeAsync("schedule.bad-zone", EmployeeRole.Worker);
		var authCookie = await SignInAsync("schedule.bad-zone");

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddVersionAsync(authCookie, cookie, token, workerId, "2026-01-01", "Bogus/NotAZone");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("That is not a recognized IANA time zone.");
	}

	[Fact]
	public async Task A_worker_cannot_view_another_employees_schedule()
	{
		var workerId = await SeedEmployeeAsync("schedule.viewer", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("schedule.viewed", EmployeeRole.Worker);
		var authCookie = await SignInAsync("schedule.viewer");

		var response = await GetAsync($"/Rota/Index?userId={otherWorkerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("may not view");
	}

	[Fact]
	public async Task An_administrator_can_add_a_schedule_version_for_another_employee()
	{
		var adminUserId = await SeedEmployeeAsync("schedule.admin", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("schedule.target", EmployeeRole.Worker);
		var authCookie = await SignInAsync("schedule.admin");

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddVersionAsync(authCookie, cookie, token, workerId, "2026-01-01", "Europe/London");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Rota version added");
	}

	[Fact]
	public async Task A_cost_viewer_cannot_open_schedule_administration()
	{
		var workerId = await SeedEmployeeAsync("schedule.target-denied", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("schedule.viewer-denied", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("schedule.viewer-denied");

		var response = await GetAsync($"/Rota/Index?userId={workerId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_version()
	{
		var workerId = await SeedEmployeeAsync("schedule.correct-version", EmployeeRole.Worker);
		var added = await seedClient.Schedules.AddScheduleVersionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"], new(2026, 1, 1), null,
				[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]),
		});
		var authCookie = await SignInAsync("schedule.correct-version");

		var (cookie, token) = await GetFormAsync(authCookie, $"/Rota/CorrectVersion?userId={workerId.Value}&versionId={added.Id.Value}");
		var response = await PostCorrectVersionAsync(
			authCookie, cookie, token, workerId, added.Id, "2026-02-01", "Europe/London", "Fixed a typo in the start date");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Rota");
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_exception()
	{
		var workerId = await SeedEmployeeAsync("schedule.correct-exception", EmployeeRole.Worker);
		var added = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
				null),
			Reason = "Public holiday",
		});
		var authCookie = await SignInAsync("schedule.correct-exception");

		var (cookie, token) = await GetFormAsync(authCookie, $"/Rota/CorrectException?userId={workerId.Value}&exceptionId={added.Id.Value}");
		var response = await PostCorrectExceptionAsync(
			authCookie, cookie, token, workerId, added.Id,
			"RemoveWorkingTime", "2026-01-03T00:00:00+00:00", "2026-01-04T00:00:00+00:00", "Wrong date entered originally");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Rota");
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
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Schedule page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Schedule page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static async Task<(string CookieHeader, string Token)> ExtractFormAsync(HttpResponseMessage response, string previousAntiforgeryCookie)
	{
		var body = await response.Content.ReadAsStringAsync();
		var cookie = FindSetCookie(response, "Antiforgery") is { } newCookie ? ExtractCookiePair(newCookie) : previousAntiforgeryCookie;
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in response body.");

		return (cookie, token);
	}

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
