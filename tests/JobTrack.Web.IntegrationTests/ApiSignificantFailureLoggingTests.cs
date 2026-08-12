namespace JobTrack.Web.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Schedules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the external HTTP API's operator-diagnosability logging: a
///     <see cref="ConcurrencyConflictException" /> or <see cref="MissingRateException" /> reaches the
///     caller as a deliberately terse problem response, but the underlying detail must still reach the
///     log stream, mirroring the delete-refusal logging <c>DeleteJobNodeTests</c> already covers for the
///     Razor Pages side.
/// </summary>
public sealed partial class ApiSignificantFailureLoggingTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.api-failure-logging-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
		rootId = bootstrap.RootJobNodeId;

		factory = new(database.ConnectionString, capturedLogEntries);
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
	public async Task Correcting_a_user_cost_rate_with_a_stale_version_logs_the_concurrency_conflict()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api-failure.concurrency.worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api-failure.concurrency.manager", EmployeeRole.RateManager);
		var authCookie = await client.SignInAsync("api-failure.concurrency.manager");
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		var addResponse = await PostJsonAsync(
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

		var response = await PostJsonAsync(
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
		capturedLogEntries.Should().Contain(entry =>
			entry.Contains("api_concurrency_conflict", StringComparison.Ordinal)
			&& entry.Contains("correlation_id=", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_cost_request_with_no_resolvable_rate_logs_the_missing_rate_failure()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api-failure.missing-rate.worker");
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Fit cabinets",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
		});
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 18, 0)),
				null),
			Reason = "Full working window for missing-rate logging test",
		});
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = Instant.FromUtc(2026, 1, 1, 17, 0),
		});
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api-failure.missing-rate.viewer", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("api-failure.missing-rate.viewer");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{leaf.Id.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
		capturedLogEntries.Should().Contain(entry =>
			entry.Contains("api_missing_rate", StringComparison.Ordinal)
			&& entry.Contains("correlation_id=", StringComparison.Ordinal));
	}



	private async Task<HttpResponseMessage> PostJsonAsync(
		string path, string authCookie, string antiforgeryCookie, string antiforgeryToken, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}









	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();



	private sealed class TestWebApplicationFactory(string identityConnectionString, ConcurrentBag<string> capturedLogEntries)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(capturedLogEntries)));
		}
	}

	private sealed class CapturingLoggerProvider(ConcurrentBag<string> capturedLogEntries) : ILoggerProvider
	{
		public ILogger CreateLogger(string categoryName) => new CapturingLogger(capturedLogEntries);

		public void Dispose() { }

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
