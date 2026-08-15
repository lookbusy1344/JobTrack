namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Direct-HTTP tests for the JSON API surface (plan §8.4): OpenAPI publication,
///     cookie-authenticated rate/schedule endpoints, and RFC 7807 problem details instead of HTML
///     login/access-denied redirects on <c>/api/*</c>. Also carries the failing OpenAPI contract
///     tests for the external HTTP API surface (plan §4.1/§4.3, ADR 0030) that is not implemented
///     yet — those tests turn green one at a time as each vertical slice lands.
/// </summary>
public sealed class HttpApiTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId administratorId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId overridableJobNodeId;
	private JobNodeId rootJobNodeId;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.api-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
		rootJobNodeId = bootstrap.RootJobNodeId;

		// Overrides target a child node, never the root (ADR 0069).
		var child = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootJobNodeId,
			Description = "Overridable leaf",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		overridableJobNodeId = child.Id;

		await IdentityTestSupport.ClearRequiresPasswordChangeAsync(SchemaProvider.Sqlite, database.ConnectionString);
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

	// The former substring-based OpenAPI checks here (route strings present via string.Contains)
	// are superseded by OpenApiContractTests.cs, which parses the document as JSON and asserts the
	// exact route set, per-operation problem-response status codes, bearer security scheme, and
	// schema redactions (remediation plan §3.2).

	[Fact]
	public async Task A_cost_viewer_can_get_rates_as_json()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.viewer", EmployeeRole.CostViewer);
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		var authCookie = await client.SignInAsync("api.rates.viewer", KnownPassword);

		var response = await client.GetAuthenticatedAsync($"/api/employees/{workerId.Value}/rates", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		jsonDocument.RootElement.GetProperty("userCostRates").GetArrayLength().Should().Be(1);
		jsonDocument.RootElement.GetProperty("userCostRates")[0].GetProperty("amountPerHour").GetDecimal().Should().Be(25m);
	}

	[Fact]
	public async Task A_worker_cannot_get_rates_and_receives_problem_details_instead_of_a_redirect()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.denied");
		var authCookie = await client.SignInAsync("api.rates.denied", KnownPassword);

		var response = await client.GetAuthenticatedAsync($"/api/employees/{workerId.Value}/rates", authCookie);
		var body = await response.Content.ReadAsStringAsync();
		var jsonDocument = JsonDocument.Parse(body);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.Forbidden);
		jsonDocument.RootElement.GetProperty("title").GetString().Should().Be("Forbidden");
		jsonDocument.RootElement.GetProperty("detail").GetString().Should().Be("You do not have permission to perform this action.");
		body.Should().NotContain(
			workerId.Value.ToString(CultureInfo.InvariantCulture),
			"the denied actor/target ids are not an IDOR oracle in the response body (remediation §2.3)");
	}

	[Fact]
	public async Task A_rate_manager_can_add_a_user_cost_rate_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.target");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api.rates.manager", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "amountPerHour": 25.00,
			  "effectiveStart": "2026-01-01T00:00:00+00:00"
			}
			""");
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		jsonDocument.RootElement.GetProperty("amountPerHour").GetDecimal().Should().Be(25m);
	}

	[Fact]
	public async Task A_rate_manager_can_correct_a_user_cost_rate_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.correct-target");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.correct-manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api.rates.correct-manager", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var addResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "amountPerHour": 25.00,
			  "effectiveStart": "2026-01-01T00:00:00+00:00"
			}
			""");
		var addedJson = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync());
		var rateId = addedJson.RootElement.GetProperty("id").GetInt64();
		var version = addedJson.RootElement.GetProperty("version").GetInt64();

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates/{rateId}/correct",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "amountPerHour": 30.00,
			    "effectiveStart": "2026-01-01T00:00:00+00:00",
			    "reason": "Corrected the agreed rate",
			    "version": {{version}}
			  }
			  """);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("amountPerHour").GetDecimal().Should().Be(30m);
		jsonDocument.RootElement.GetProperty("version").GetInt64().Should().Be(version + 1);
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_with_a_stale_version_is_rejected_as_a_conflict()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.correct-stale-target");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.correct-stale-manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api.rates.correct-stale-manager", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var addResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "amountPerHour": 25.00,
			  "effectiveStart": "2026-01-01T00:00:00+00:00"
			}
			""");
		var addedJson = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync());
		var rateId = addedJson.RootElement.GetProperty("id").GetInt64();
		var staleVersion = addedJson.RootElement.GetProperty("version").GetInt64() + 1;

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates/{rateId}/correct",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "amountPerHour": 30.00,
			    "effectiveStart": "2026-01-01T00:00:00+00:00",
			    "reason": "Corrected the agreed rate",
			    "version": {{staleVersion}}
			  }
			  """);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task Retrying_an_identical_add_user_cost_rate_request_is_rejected_as_a_conflict_not_duplicated()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.retry");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.retry-manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api.rates.retry-manager", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		const string requestBody = """
								   {
								     "amountPerHour": 25.00,
								     "effectiveStart": "2026-01-01T00:00:00+00:00"
								   }
								   """;

		var firstResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);
		var retryResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);
		var retryBody = await retryResponse.Content.ReadAsStringAsync();
		var retryJsonDocument = JsonDocument.Parse(retryBody);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
		retryJsonDocument.RootElement.GetProperty("type").GetString().Should().Be("/problems/invariant-violation");
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_without_an_antiforgery_token_is_rejected()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.csrf");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.csrf-manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api.rates.csrf-manager", KnownPassword);

		var response = await client.PostJsonWithoutAntiforgeryAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates",
			authCookie,
			"""
			{
			  "amountPerHour": 25.00,
			  "effectiveStart": "2026-01-01T00:00:00+00:00"
			}
			""");
		var body = await response.Content.ReadAsStringAsync();
		var jsonDocument = JsonDocument.Parse(body);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("type").GetString().Should().Be("/problems/validation");
		jsonDocument.RootElement.GetProperty("detail").GetString().Should().Be("The request failed CSRF validation.");
		body.Should().NotContain("antiforgery", "raw framework validation exception text is not part of the public API contract");
		body.Should().NotContain("required", "raw framework validation exception text is not part of the public API contract");
	}

	[Fact]
	public async Task Adding_a_node_rate_override_without_an_antiforgery_token_is_rejected()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.override-csrf");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.override-csrf-manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api.rates.override-csrf-manager", KnownPassword);

		var response = await client.PostJsonWithoutAntiforgeryAsync(
			$"/api/employees/{workerId.Value}/rates/node-rate-overrides",
			authCookie,
			$$"""
			  {
			    "nodeId": {{administratorId.Value}},
			    "amountPerHour": 30.00,
			    "effectiveStart": "2026-01-01T00:00:00+00:00"
			  }
			  """);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task An_administrator_can_correct_a_node_rate_override_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.correct-override-target");
		var authCookie = await client.SignInAsync("admin.api-tests", AdministratorPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var addResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/node-rate-overrides",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "nodeId": {{overridableJobNodeId.Value}},
			    "amountPerHour": 40.00,
			    "effectiveStart": "2026-01-01T00:00:00+00:00"
			  }
			  """);
		var addedJson = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync());
		var overrideId = addedJson.RootElement.GetProperty("id").GetInt64();
		var version = addedJson.RootElement.GetProperty("version").GetInt64();

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/node-rate-overrides/{overrideId}/correct",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "nodeId": {{overridableJobNodeId.Value}},
			    "amountPerHour": 45.00,
			    "effectiveStart": "2026-01-01T00:00:00+00:00",
			    "reason": "Corrected the override rate",
			    "version": {{version}}
			  }
			  """);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("amountPerHour").GetDecimal().Should().Be(45m);
	}

	[Fact]
	public async Task Adding_a_schedule_version_without_an_antiforgery_token_is_rejected()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.csrf-worker");
		var authCookie = await client.SignInAsync("api.schedule.csrf-worker", KnownPassword);

		var response = await client.PostJsonWithoutAntiforgeryAsync(
			$"/api/employees/{workerId.Value}/schedule/versions",
			authCookie,
			"""
			{
			  "ianaTimeZone": "Europe/London",
			  "effectiveStart": "2026-01-01",
			  "weeklyIntervals": [
			    {
			      "day": "Monday",
			      "start": "09:00:00",
			      "end": "17:00:00"
			    }
			  ]
			}
			""");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Adding_a_schedule_exception_without_an_antiforgery_token_is_rejected()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.csrf-exception");
		var authCookie = await client.SignInAsync("api.schedule.csrf-exception", KnownPassword);

		var response = await client.PostJsonWithoutAntiforgeryAsync(
			$"/api/employees/{workerId.Value}/schedule/exceptions",
			authCookie,
			"""
			{
			  "effect": "Unavailable",
			  "start": "2026-02-01T09:00:00+00:00",
			  "end": "2026-02-01T17:00:00+00:00",
			  "reason": "Public holiday"
			}
			""");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_with_an_invalid_antiforgery_token_is_rejected()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.bad-token");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.bad-token-manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api.rates.bad-token-manager", KnownPassword);
		var (antiforgeryCookie, _) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates",
			authCookie,
			antiforgeryCookie,
			"this-is-not-a-valid-antiforgery-token",
			"""
			{
			  "amountPerHour": 25.00,
			  "effectiveStart": "2026-01-01T00:00:00+00:00"
			}
			""");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task An_overlapping_rate_returns_problem_details_without_provider_leakage()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.rates.overlap");
		var authCookie = await client.SignInAsync("admin.api-tests", AdministratorPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/rates/user-cost-rates",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "amountPerHour": 30.00,
			  "effectiveStart": "2026-06-01T00:00:00+00:00"
			}
			""");
		var body = await response.Content.ReadAsStringAsync();
		var jsonDocument = JsonDocument.Parse(body);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("type").GetString().Should().Be("/problems/invariant-violation");
		jsonDocument.RootElement.GetProperty("detail").GetString()
					.Should().Be("The request conflicts with an existing record or violates a data constraint.");
		body.Should().NotContain("SQLITE");
		body.Should().NotContain("sqlite");
		body.Should().NotContain("25.00");
		body.Should().NotContain("30.00");
	}

	[Fact]
	public async Task A_worker_can_add_their_own_schedule_version_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.worker");
		var authCookie = await client.SignInAsync("api.schedule.worker", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/versions",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "ianaTimeZone": "Europe/London",
			  "effectiveStart": "2026-01-01",
			  "weeklyIntervals": [
			    {
			      "day": "Monday",
			      "start": "09:00:00",
			      "end": "17:00:00"
			    }
			  ]
			}
			""");
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		jsonDocument.RootElement.GetProperty("ianaTimeZone").GetString().Should().Be("Europe/London");
		jsonDocument.RootElement.GetProperty("weeklyIntervals").GetArrayLength().Should().Be(1);
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_version_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.correct-worker");
		var authCookie = await client.SignInAsync("api.schedule.correct-worker", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var addResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/versions",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "ianaTimeZone": "Europe/London",
			  "effectiveStart": "2026-01-01",
			  "weeklyIntervals": [
			    {
			      "day": "Monday",
			      "start": "09:00:00",
			      "end": "17:00:00"
			    }
			  ]
			}
			""");
		var addedJson = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync());
		var versionId = addedJson.RootElement.GetProperty("id").GetInt64();
		var version = addedJson.RootElement.GetProperty("version").GetInt64();

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/versions/{versionId}/correct",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "ianaTimeZone": "Europe/London",
			    "effectiveStart": "2026-02-01",
			    "weeklyIntervals": [
			      {
			        "day": "Monday",
			        "start": "09:00:00",
			        "end": "17:00:00"
			      }
			    ],
			    "reason": "Fixed a typo in the start date",
			    "version": {{version}}
			  }
			  """);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("effectiveStart").GetString().Should().Be("2026-02-01");
		jsonDocument.RootElement.GetProperty("version").GetInt64().Should().Be(version + 1);
	}

	[Fact]
	public async Task Adding_a_schedule_version_with_a_retired_tzdb_alias_persists_the_canonical_zone_id()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.alias-worker");
		var authCookie = await client.SignInAsync("api.schedule.alias-worker", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/versions",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "ianaTimeZone": "Asia/Calcutta",
			  "effectiveStart": "2026-01-01",
			  "weeklyIntervals": [
			    {
			      "day": "Monday",
			      "start": "09:00:00",
			      "end": "17:00:00"
			    }
			  ]
			}
			""");
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		jsonDocument.RootElement.GetProperty("ianaTimeZone").GetString().Should().Be("Asia/Kolkata");
	}

	[Fact]
	public async Task A_stored_but_now_unrecognized_schedule_zone_is_a_server_fault_not_a_bad_request()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.zone-rot-worker");
		var authCookie = await client.SignInAsync("api.schedule.zone-rot-worker", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		_ = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/versions",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "ianaTimeZone": "Europe/London",
			  "effectiveStart": "2026-01-01",
			  "weeklyIntervals": [
			    {
			      "day": "Monday",
			      "start": "09:00:00",
			      "end": "17:00:00"
			    }
			  ]
			}
			""");

		await using (var connection = new SqliteConnection(database.ConnectionString)) {
			await connection.OpenAsync();
			await using var command = connection.CreateCommand();
			command.CommandText = "UPDATE user_schedule_version SET iana_time_zone = 'Bogus/NotAZone' WHERE user_id = @userId;";
			command.Parameters.AddWithValue("@userId", workerId.Value);
			_ = await command.ExecuteNonQueryAsync();
		}

		var response = await client.GetAuthenticatedAsync($"/api/employees/{workerId.Value}/schedule", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.InternalServerError);
	}

	[Fact]
	public async Task Retrying_an_identical_add_schedule_version_request_is_rejected_as_a_conflict_not_duplicated()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.retry-worker");
		var authCookie = await client.SignInAsync("api.schedule.retry-worker", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		const string requestBody = """
								   {
								     "ianaTimeZone": "Europe/London",
								     "effectiveStart": "2026-01-01",
								     "weeklyIntervals": [
								       {
								         "day": "Monday",
								         "start": "09:00:00",
								         "end": "17:00:00"
								       }
								     ]
								   }
								   """;

		var firstResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/versions", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);
		var retryResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/versions", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);
		var retryBody = await retryResponse.Content.ReadAsStringAsync();
		var retryJsonDocument = JsonDocument.Parse(retryBody);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
		retryJsonDocument.RootElement.GetProperty("type").GetString().Should().Be("/problems/invariant-violation");
	}

	[Fact]
	public async Task Retrying_an_identical_add_schedule_exception_request_is_rejected_as_a_conflict_not_duplicated()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.exception-retry-worker");
		var authCookie = await client.SignInAsync("api.schedule.exception-retry-worker", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		const string requestBody = """
								   {
								     "effect": "RemoveWorkingTime",
								     "start": "2026-02-01T09:00:00+00:00",
								     "end": "2026-02-01T17:00:00+00:00",
								     "reason": "Public holiday"
								   }
								   """;

		var firstResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/exceptions", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);
		var retryResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/exceptions", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task A_worker_can_correct_their_own_schedule_exception_via_the_api()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.schedule.correct-exception-worker");
		var authCookie = await client.SignInAsync("api.schedule.correct-exception-worker", KnownPassword);
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);
		var addResponse = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/exceptions",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			"""
			{
			  "effect": "RemoveWorkingTime",
			  "start": "2026-02-01T09:00:00+00:00",
			  "end": "2026-02-01T17:00:00+00:00",
			  "reason": "Public holiday"
			}
			""");
		var addedJson = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync());
		var exceptionId = addedJson.RootElement.GetProperty("id").GetInt64();
		var version = addedJson.RootElement.GetProperty("version").GetInt64();

		var response = await client.PostJsonAsync(
			$"/api/employees/{workerId.Value}/schedule/exceptions/{exceptionId}/correct",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "effect": "RemoveWorkingTime",
			    "start": "2026-02-03T09:00:00+00:00",
			    "end": "2026-02-03T17:00:00+00:00",
			    "reason": "Wrong date entered originally",
			    "version": {{version}}
			  }
			  """);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("reason").GetString().Should().Be("Wrong date entered originally");
		jsonDocument.RootElement.GetProperty("version").GetInt64().Should().Be(version + 1);
	}

	/// <summary>
	///     Direct-HTTP tests for the PAT bearer authentication scheme (ADR 0029): the same rate
	///     endpoints already covered by cookie-authenticated tests above, exercised instead with
	///     <c>Authorization: Bearer</c>, proving the bearer scheme reaches the identical
	///     authorization/ownership pipeline (row 15) rather than a parallel one, and that antiforgery
	///     (a cookie-only threat, row 4) is not required for it.
	/// </summary>
	[Fact]
	public async Task A_cost_viewer_can_get_rates_as_json_via_a_bearer_token()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.worker");
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.viewer", EmployeeRole.CostViewer);
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		var token = await IssueTokenAsync(viewerId);

		var response = await client.GetWithBearerAsync($"/api/employees/{workerId.Value}/rates", token);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task A_worker_with_a_valid_bearer_token_is_still_denied_rates_access()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.denied");
		var token = await IssueTokenAsync(workerId);

		var response = await client.GetWithBearerAsync($"/api/employees/{workerId.Value}/rates", token);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task A_rate_manager_can_add_a_user_cost_rate_via_a_bearer_token_without_an_antiforgery_token()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.mutate-worker");
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.mutate-manager", EmployeeRole.RateManager);
		var token = await IssueTokenAsync(managerId);

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/employees/{workerId.Value}/rates/user-cost-rates");
		request.Headers.Authorization = new("Bearer", token);
		request.Content = new StringContent(
			"""
			{
			  "amountPerHour": 25.00,
			  "effectiveStart": "2026-01-01T00:00:00+00:00"
			}
			""", Encoding.UTF8, "application/json");
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	[Fact]
	public async Task A_malformed_bearer_token_is_rejected_with_401()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.malformed", EmployeeRole.CostViewer);

		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", "not-a-real-token");

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task An_empty_bearer_token_is_rejected_with_401()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.empty", EmployeeRole.CostViewer);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/employees/{viewerId.Value}/rates");
		request.Headers.TryAddWithoutValidation("Authorization", "Bearer ");
		var response = await client.SendAsync(request);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task A_missing_authorization_header_is_rejected_with_401()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.missing", EmployeeRole.CostViewer);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/employees/{viewerId.Value}/rates");
		var response = await client.SendAsync(request);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task An_expired_bearer_token_is_rejected_with_401()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.expired", EmployeeRole.CostViewer);
		var token = await IssueTokenAsync(viewerId);
		await ExpireMostRecentTokenAsync(viewerId);

		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", token);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task A_revoked_bearer_token_is_rejected_with_401()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.revoked", EmployeeRole.CostViewer);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() {
				Actor = viewerId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			Label = "test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});
		await seedClient.Tokens.RevokeAsync(new() {
			Context = new() {
				Actor = viewerId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			TokenId = issued.Id,
		});

		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", issued.Token);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task A_bearer_token_for_a_disabled_account_is_rejected_with_401()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.disabled", EmployeeRole.CostViewer);
		var token = await IssueTokenAsync(viewerId);
		_ = await seedClient.Employees.SetEnabledAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			Enabled = false,
		});

		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", token);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task Disabling_an_employee_revokes_their_personal_access_tokens()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.disable-revokes", EmployeeRole.CostViewer);
		var token = await IssueTokenAsync(viewerId);

		_ = await seedClient.Employees.SetEnabledAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			Enabled = false,
		});
		_ = await seedClient.Employees.SetEnabledAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			Enabled = true,
		});
		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", token);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task Assigning_a_role_to_an_employee_revokes_their_personal_access_tokens()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.role-assign-revokes", EmployeeRole.CostViewer);
		var token = await IssueTokenAsync(viewerId);

		_ = await seedClient.Employees.AssignRoleAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			Role = EmployeeRole.RateManager,
		});
		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", token);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task Revoking_a_role_from_an_employee_revokes_their_personal_access_tokens()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.role-revoke-revokes", EmployeeRole.CostViewer);
		var token = await IssueTokenAsync(viewerId);

		_ = await seedClient.Employees.RevokeRoleAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			Role = EmployeeRole.CostViewer,
		});
		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", token);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	[Fact]
	public async Task Resetting_an_employees_password_revokes_their_personal_access_tokens()
	{
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.bearer.password-reset-revokes", EmployeeRole.CostViewer);
		var token = await IssueTokenAsync(viewerId);

		_ = await seedClient.Employees.ResetPasswordAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = viewerId,
			TargetUserName = "api.bearer.password-reset-revokes",
			NewPassword = "Reset-Horse-Battery-99!",
		});
		var response = await client.GetWithBearerAsync($"/api/employees/{viewerId.Value}/rates", token);

		await AssertAuthenticationRequiredProblemAsync(response);
	}

	/// <summary>
	///     Every bearer-authentication failure (missing, empty, malformed, expired, revoked, or a
	///     disabled account's token) returns the identical problem body regardless of cause (remediation
	///     plan §3.3): same status, content type, and stable problem <c>type</c>, so a caller cannot
	///     distinguish "token expired" from "token revoked" from "account disabled" by inspecting the
	///     response.
	/// </summary>
	private static async Task AssertAuthenticationRequiredProblemAsync(HttpResponseMessage response)
	{
		var body = await response.Content.ReadAsStringAsync();
		var jsonDocument = JsonDocument.Parse(body);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		jsonDocument.RootElement.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.Unauthorized);
		jsonDocument.RootElement.GetProperty("title").GetString().Should().Be("Authentication required");
		jsonDocument.RootElement.GetProperty("type").GetString().Should().Be("/problems/authentication-required");
	}

	private async Task<string> IssueTokenAsync(AppUserId userId)
	{
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() {
				Actor = userId,
				CorrelationId = Guid.NewGuid(),
			},
			TargetUserId = userId,
			Label = "test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		return issued.Token;
	}

	private async Task ExpireMostRecentTokenAsync(AppUserId userId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  UPDATE personal_access_token
							  SET created_at = $createdAt, expires_at = $expiresAt
							  WHERE id = (SELECT id FROM personal_access_token WHERE app_user_id = $userId ORDER BY created_at DESC LIMIT 1);
							  """;
		var now = SystemClock.Instance.GetCurrentInstant();
		_ = command.Parameters.AddWithValue("$createdAt", (now - Duration.FromDays(2)).ToUnixTimeTicks());
		_ = command.Parameters.AddWithValue("$expiresAt", (now - Duration.FromDays(1)).ToUnixTimeTicks());
		_ = command.Parameters.AddWithValue("$userId", userId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}
}
