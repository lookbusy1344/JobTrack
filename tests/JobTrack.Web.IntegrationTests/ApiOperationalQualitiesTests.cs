namespace JobTrack.Web.IntegrationTests;

using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the external HTTP API's operational qualities (plan §4.4): per-client/
///     per-user rate limiting distinct from login throttling, and bounded per-request telemetry that
///     never carries a rate or cost value into the log stream.
/// </summary>
public sealed partial class ApiOperationalQualitiesTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private readonly ConcurrentBag<string> capturedLogEntries = [];

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
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.api-ops-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
		rootId = bootstrap.RootJobNodeId;

		factory = new(database.ConnectionString, capturedLogEntries);
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
	public async Task Exceeding_the_per_user_api_rate_limit_returns_429_with_problem_details()
	{
		var workerId = await SeedEmployeeAsync("api-ops.limited");
		var authCookie = await SignInAsync("api-ops.limited");

		HttpResponseMessage? rejected = null;
		for (var i = 0; i < 10 && rejected is null; i++) {
			var response = await GetAsync($"/api/jobs/{rootId.Value}", authCookie);
			if (response.StatusCode == HttpStatusCode.TooManyRequests) {
				rejected = response;
			}
		}

		rejected.Should().NotBeNull("the test-configured permit limit is well below the number of requests attempted");
		rejected!.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Two_different_users_each_get_their_own_rate_limit_budget()
	{
		var firstWorkerId = await SeedEmployeeAsync("api-ops.first");
		var secondWorkerId = await SeedEmployeeAsync("api-ops.second");
		var firstCookie = await SignInAsync("api-ops.first");
		var secondCookie = await SignInAsync("api-ops.second");

		// Exhaust the first user's entire configured budget (3) -- if partitioning were broken
		// and every caller shared one bucket, this would also exhaust the second user's budget.
		for (var i = 0; i < 3; i++) {
			var firstUsersResponse = await GetAsync($"/api/jobs/{rootId.Value}", firstCookie);
			firstUsersResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		}

		var secondUsersResponse = await GetAsync($"/api/jobs/{rootId.Value}", secondCookie);

		secondUsersResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a different user's own budget is untouched by another user's usage");
	}

	[Fact]
	public async Task A_successful_api_request_logs_bounded_fields_but_never_the_returned_rate_value()
	{
		var workerId = await SeedEmployeeAsync("api-ops.telemetry-worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		const decimal DistinctiveRateAmount = 137.42m;
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Rate = new(new(DistinctiveRateAmount), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		_ = await SeedEmployeeAsync("api-ops.telemetry-viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("api-ops.telemetry-viewer");

		var response = await GetAsync($"/api/employees/{workerId.Value}/rates", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(DistinctiveRateAmount.ToString("F2", CultureInfo.InvariantCulture),
			"the rate is legitimately part of the authorized response body");

		var apiRequestEntries = capturedLogEntries.Where(entry => entry.Contains("api_request", StringComparison.Ordinal)).ToArray();
		apiRequestEntries.Should().NotBeEmpty();
		apiRequestEntries.Should().Contain(entry =>
			entry.Contains("correlation_id=", StringComparison.Ordinal) && entry.Contains("duration_ms=", StringComparison.Ordinal) &&
			entry.Contains("status_code=200", StringComparison.Ordinal));
		capturedLogEntries.Should()
			.NotContain(entry => entry.Contains(DistinctiveRateAmount.ToString("F2", CultureInfo.InvariantCulture), StringComparison.Ordinal),
				"the rate value must never reach the telemetry log line, only the authorized HTTP response body");
	}

	[Fact]
	public async Task A_failing_api_request_logs_a_stable_failure_category_not_the_exception_message()
	{
		var workerId = await SeedEmployeeAsync("api-ops.failure-worker");
		var authCookie = await SignInAsync("api-ops.failure-worker");

		var response = await GetAsync("/api/jobs/999999999", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		capturedLogEntries.Should().Contain(entry =>
			entry.Contains("api_request", StringComparison.Ordinal)
			&& entry.Contains("status_code=404", StringComparison.Ordinal)
			&& entry.Contains("failure_category=/problems/entity-not-found", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Success_and_problem_responses_carry_the_expected_content_type_across_route_categories()
	{
		// Remediation plan §3.7: a content-type matrix across route categories, not just the
		// handful of ad hoc checks scattered through the other API test files.
		_ = await SeedEmployeeAsync("api-ops.content-type.worker");
		var authCookie = await SignInAsync("api-ops.content-type.worker");

		var successNode = await GetAsync($"/api/jobs/{rootId.Value}", authCookie);
		var notFoundNode = await GetAsync("/api/jobs/999999999", authCookie);
		var validationError = await GetAsync("/api/jobs/search", authCookie);
		var unauthenticated = await client.GetAsync($"/api/jobs/{rootId.Value}");

		successNode.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		notFoundNode.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		validationError.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		unauthenticated.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string userName, EmployeeRole role = EmployeeRole.Worker)
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

	private sealed class TestWebApplicationFactory(string identityConnectionString, ConcurrentBag<string> capturedLogEntries)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			// A low, test-only budget so the rate-limit tests don't need dozens of real requests
			// to observe a 429 -- production keeps the unconfigured default (see Program.cs).
			_ = builder.UseSetting("RateLimiting:ApiPermitLimit", "3");
			_ = builder.UseSetting("RateLimiting:ApiWindowSeconds", "60");
			_ = builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(capturedLogEntries)));
		}
	}

	private sealed class CapturingLoggerProvider(ConcurrentBag<string> capturedLogEntries) : ILoggerProvider
	{
		public ILogger CreateLogger(string categoryName) => new CapturingLogger(capturedLogEntries);

		public void Dispose()
		{
		}

		private sealed class CapturingLogger(ConcurrentBag<string> capturedLogEntries) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				capturedLogEntries.Add(formatter(state, exception));
				if (exception is not null) {
					capturedLogEntries.Add(exception.ToString());
				}
			}
		}
	}
}
