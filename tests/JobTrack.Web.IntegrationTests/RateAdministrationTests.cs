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
///     Direct-HTTP tests for employee rate administration (plan §8.5 slice 7, spec §9): adding a user
///     cost rate and a node rate override, and the split between <c>RateManager</c>'s write-only
///     permission and <c>CostViewer</c>'s (or <c>Administrator</c>'s) read visibility.
/// </summary>
public sealed partial class RateAdministrationTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId rootJobNodeId;
	private JobNodeId overridableJobNodeId;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.rate-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootJobNodeId = bootstrap.RootJobNodeId;

		// Overrides target a child node, never the root (ADR 0069).
		var child = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootJobNodeId,
			Description = "Overridable leaf",
			OwnerUserId = bootstrap.AdministratorId,
			Priority = Priority.Medium,
		});
		overridableJobNodeId = child.Id;

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
	public async Task An_administrator_can_add_a_user_cost_rate_and_a_node_rate_override()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.admin", EmployeeRole.Administrator);
		var authCookie = await SignInAsync("rates.admin", KnownPassword);

		var (rateCookie, rateToken) = await GetFormAsync(authCookie, workerId);
		var rateResponse = await PostAddUserCostRateAsync(authCookie, rateCookie, rateToken, workerId, "25.00", "2026-01-01T00:00");
		rateResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var rateReloaded = await client.FollowRedirectAsync(rateResponse, authCookie);
		var rateBody = await rateReloaded.Content.ReadAsStringAsync();

		rateBody.Should().Contain("User cost rate added");

		var (overrideCookie, overrideToken) = await WebTestHttp.ExtractFormAsync(rateReloaded, rateCookie);
		var overrideResponse = await PostAddNodeRateOverrideAsync(
			authCookie, overrideCookie, overrideToken, workerId, overridableJobNodeId, "30.00", "2026-01-01T00:00");
		overrideResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var overrideReloaded = await client.FollowRedirectAsync(overrideResponse, authCookie);
		var overrideBody = await overrideReloaded.Content.ReadAsStringAsync();

		overrideBody.Should().Contain("Node rate override added");
	}

	[Fact]
	public async Task A_rate_manager_can_add_a_rate_but_cannot_view_existing_rates()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.target");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.manager", EmployeeRole.RateManager);
		var authCookie = await SignInAsync("rates.manager", KnownPassword);

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddUserCostRateAsync(authCookie, cookie, token, workerId, "25.00", "2026-01-01T00:00");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("User cost rate added");
		body.Should().NotContain("may not view");
		body.Should().Contain("Current rates are hidden");
		body.Should().NotContain("User cost rates");
	}

	[Fact]
	public async Task A_worker_cannot_open_rate_administration()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.self");
		var authCookie = await SignInAsync("rates.self", KnownPassword);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Admin/Rates?userId={workerId.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_cost_viewer_can_view_rates_but_cannot_see_write_controls()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.viewed");
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = await CreateContextForAsync("admin.rate-tests"),
			UserId = workerId,
			Rate = new(
				new(25m),
				Instant.FromUtc(2026, 1, 1, 0, 0),
				null),
		});
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("rates.viewer", KnownPassword);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Admin/Rates?userId={workerId.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("25.00");
		body.Should().NotContain("Add a user cost rate");
		body.Should().NotContain("Add a node rate override");
	}

	[Fact]
	public async Task An_administrator_can_correct_a_user_cost_rate()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.correct-target");
		var added = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = await CreateContextForAsync("admin.rate-tests"),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.correct-manager", EmployeeRole.Administrator);
		var authCookie = await SignInAsync("rates.correct-manager", KnownPassword);

		var (cookie, token) = await GetFormAsync(authCookie, $"/Admin/CorrectUserCostRate?userId={workerId.Value}&rateId={added.Id.Value}");
		var response = await PostCorrectUserCostRateAsync(
			authCookie, cookie, token, workerId, added.Id, "30.00", "2026-01-01T00:00", "Corrected the agreed rate");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Admin/Rates");
	}

	[Fact]
	public async Task An_administrator_can_correct_a_node_rate_override()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.correct-override-target");
		var added = await seedClient.Rates.AddNodeRateOverrideAsync(new() {
			Context = await CreateContextForAsync("admin.rate-tests"),
			UserId = workerId,
			Override = new(
				overridableJobNodeId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.correct-override-manager", EmployeeRole.Administrator);
		var authCookie = await SignInAsync("rates.correct-override-manager", KnownPassword);

		var (cookie, token) = await GetFormAsync(
			authCookie, $"/Admin/CorrectNodeRateOverride?userId={workerId.Value}&overrideId={added.Id.Value}");
		var response = await PostCorrectNodeRateOverrideAsync(
			authCookie, cookie, token, workerId, added.Id, overridableJobNodeId, "45.00", "2026-01-01T00:00", "Corrected the override rate");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Admin/Rates");
	}

	/// <summary>
	///     §2.4: the correction form's <c>EffectiveStart</c> round-trips a stored instant back to a
	///     <c>datetime-local</c> string in the acting administrator's own zone on GET, and a malformed
	///     resubmission is rejected without applying the correction.
	/// </summary>
	[Fact]
	public async Task The_correct_user_cost_rate_form_prefills_in_the_actors_zone_and_rejects_a_malformed_resubmission()
	{
		var newYork = DateTimeZoneProviders.Tzdb["America/New_York"];
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.correct-zoned-target");
		var stored = CivilTimeResolver.ToInstant(new(2026, 1, 1, 9, 0, 0), newYork);
		var added = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = await CreateContextForAsync("admin.rate-tests"),
			UserId = workerId,
			Rate = new(new(25m), stored, null),
		});
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.correct-zoned-manager", EmployeeRole.Administrator, "America/New_York");
		var authCookie = await SignInAsync("rates.correct-zoned-manager", KnownPassword);

		var (cookie, token) = await GetFormAsync(authCookie, $"/Admin/CorrectUserCostRate?userId={workerId.Value}&rateId={added.Id.Value}");
		var getResponse = await GetAsync(authCookie, $"/Admin/CorrectUserCostRate?userId={workerId.Value}&rateId={added.Id.Value}");
		(await getResponse.Content.ReadAsStringAsync()).Should().Contain("value=\"2026-01-01T09:00\"");

		var response = await PostCorrectUserCostRateAsync(
			authCookie, cookie, token, workerId, added.Id, "30.00", "not-a-local-date-time", "Malformed correction");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Enter a valid date and time.");

		var snapshot = await seedClient.Query.GetRatesAsync(
			new() {
				Context = await CreateContextForAsync("admin.rate-tests"),
				UserId = workerId,
			}, CancellationToken.None);
		snapshot.UserCostRates.Single().Rate.EffectiveStart.Should().Be(stored);
	}

	/// <summary>§2.4: a malformed <c>EffectiveStart</c> is rejected before the rate command runs.</summary>
	[Fact]
	public async Task A_malformed_EffectiveStart_on_add_user_cost_rate_is_rejected_without_adding_the_rate()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.malformed-effective");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.malformed-admin", EmployeeRole.Administrator);
		var authCookie = await SignInAsync("rates.malformed-admin", KnownPassword);

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddUserCostRateAsync(authCookie, cookie, token, workerId, "25.00", "not-a-local-date-time");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Enter a valid date and time.");
		body.Should().NotContain("User cost rate added");
	}

	/// <summary>
	///     §2.4: <c>EffectiveStart</c> is a bare wall-clock string resolved in the acting administrator's
	///     own zone, not the server process's own OS zone. The administrator here is seeded with
	///     <c>America/New_York</c>, deliberately different from whatever zone this test process itself
	///     runs in, so the assertion only holds if resolution actually used the actor's zone.
	/// </summary>
	[Fact]
	public async Task EffectiveStart_is_resolved_in_the_acting_administrators_own_zone_not_the_server_process_zone()
	{
		var newYork = DateTimeZoneProviders.Tzdb["America/New_York"];
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.zoned-effective");
		var administratorId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "rates.zoned-admin", EmployeeRole.Administrator, "America/New_York");
		var authCookie = await SignInAsync("rates.zoned-admin", KnownPassword);

		var (cookie, token) = await GetFormAsync(authCookie, workerId);
		var response = await PostAddUserCostRateAsync(authCookie, cookie, token, workerId, "25.00", "2026-06-15T09:00");
		response.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var snapshot = await seedClient.Query.GetRatesAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				UserId = workerId,
			}, CancellationToken.None);

		snapshot.UserCostRates.Single().Rate.EffectiveStart.Should().Be(CivilTimeResolver.ToInstant(new(2026, 6, 15, 9, 0, 0), newYork));
	}

	private async Task<HttpResponseMessage> PostCorrectUserCostRateAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId, UserCostRateId rateId,
		string amountPerHour, string effectiveStart, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/CorrectUserCostRate");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["RateId"] = rateId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.AmountPerHour"] = amountPerHour,
			["Input.EffectiveStart"] = effectiveStart,
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostCorrectNodeRateOverrideAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId, NodeRateOverrideId overrideId,
		JobNodeId nodeId, string amountPerHour, string effectiveStart, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/CorrectNodeRateOverride");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["OverrideId"] = overrideId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.NodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.AmountPerHour"] = amountPerHour,
			["Input.EffectiveStart"] = effectiveStart,
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostAddUserCostRateAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId, string amountPerHour, string effectiveStart)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/Rates?handler=AddUserCostRate");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["UserCostRateInput.AmountPerHour"] = amountPerHour,
			["UserCostRateInput.EffectiveStart"] = effectiveStart,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostAddNodeRateOverrideAsync(
		string authCookie, string antiforgeryCookie, string token, AppUserId userId, JobNodeId nodeId,
		string amountPerHour, string effectiveStart)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/Rates?handler=AddNodeRateOverride");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["UserId"] = userId.Value.ToString(CultureInfo.InvariantCulture),
			["NodeRateOverrideInput.NodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["NodeRateOverrideInput.AmountPerHour"] = amountPerHour,
			["NodeRateOverrideInput.EffectiveStart"] = effectiveStart,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> GetAsync(string authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, AppUserId userId) =>
		await GetFormAsync(authCookie, $"/Admin/Rates?userId={userId.Value}");

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Rates page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Rates page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<CommandContext> CreateContextForAsync(string userName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT app_user_id
							  FROM identity_user
							  WHERE user_name = $userName;
							  """;
		_ = command.Parameters.AddWithValue("$userName", userName);
		var appUserId = (long?)await command.ExecuteScalarAsync()
						?? throw new InvalidOperationException($"User '{userName}' was not found.");

		return new() {
			Actor = new(appUserId),
			CorrelationId = Guid.NewGuid(),
		};
	}



	private async Task<string> SignInAsync(string userName, string password)
	{
		var (antiforgeryCookie, token) = await client.GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = WebTestHttp.FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}



	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();
}
