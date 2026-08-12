namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Rates;
using Domain.Schedules;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Direct-HTTP tests for cost reports with rate provenance and current prerequisite diagnostics
///     (plan §8.5 slice 8, spec §718 workflow 8): a cost viewer sees the segment-by-segment rate trace
///     and readiness blockers for a leaf, while a worker without cost-viewing permission is denied.
/// </summary>
public sealed partial class CostReportTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private static readonly Instant SessionStart = Instant.FromUtc(2026, 1, 1, 9, 0);
	private static readonly Instant SessionFinish = Instant.FromUtc(2026, 1, 1, 11, 0);

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
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
			UserName = "admin.cost-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootJobNodeId = bootstrap.RootJobNodeId;

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
	public async Task A_cost_viewer_sees_rate_provenance_and_readiness_for_a_leaf()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.viewer", EmployeeRole.CostViewer);
		var leafId = await SeedLeafWithCostedSessionAsync(administratorId, workerId);
		var authCookie = await client.SignInAsync("cost.viewer");

		var response = await client.GetAuthenticatedAsync($"/Jobs/CostReport?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		SterlingAmountPattern().Count(body).Should().Be(3);
		body.Should().Contain(">&#xA3;120.00<");
		body.Should().Contain("Allocated time");
		body.Should().Contain(">2.0 hrs<");
		body.Should().Contain(nameof(RateSource.UserCostRate));
		body.Should().Contain("No blocks");
	}

	[Fact]
	public async Task A_worker_cannot_open_the_cost_report()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.self");
		var leafId = await SeedLeafWithCostedSessionAsync(administratorId, workerId);
		var authCookie = await client.SignInAsync("cost.self");

		var response = await client.GetAuthenticatedAsync($"/Jobs/CostReport?nodeId={leafId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task Requesting_a_nonexistent_node_shows_an_error_instead_of_failing()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.viewer-missing", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("cost.viewer-missing");

		var response = await client.GetAuthenticatedAsync("/Jobs/CostReport?nodeId=999999", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("does not exist");
	}

	/// <summary>§2.4: a malformed <c>AsOf</c> is rejected before the cost query runs, never silently defaulted to "now".</summary>
	[Fact]
	public async Task A_malformed_AsOf_is_rejected_without_running_the_report()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.malformed-asof-worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.malformed-asof-viewer", EmployeeRole.CostViewer);
		var leafId = await SeedLeafWithCostedSessionAsync(administratorId, workerId);
		var authCookie = await client.SignInAsync("cost.malformed-asof-viewer");

		var response = await client.GetAuthenticatedAsync($"/Jobs/CostReport?nodeId={leafId.Value}&AsOf=not-a-local-date-time", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Enter a valid date and time.");
		body.Should().NotContain("&#xA3;");
	}

	/// <summary>
	///     §2.4: <c>AsOf</c> is a bare wall-clock string resolved in the viewing employee's own zone, not
	///     the server process's own OS zone. The costed session runs 09:00-11:00 UTC; the viewer here is
	///     seeded with <c>America/New_York</c> (UTC-5 in January), so <c>08:00</c> local resolves to
	///     13:00 UTC -- after the session finished, so the full session is costed. Misreading that same
	///     literal as a UTC time would land at 08:00 UTC, before the session even started, and see no
	///     costed segments -- so this only passes if resolution actually used the viewer's zone.
	/// </summary>
	[Fact]
	public async Task AsOf_is_resolved_in_the_viewing_employees_own_zone_not_the_server_process_zone()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.zoned-asof-worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.zoned-asof-viewer", EmployeeRole.CostViewer, "America/New_York");
		var leafId = await SeedLeafWithCostedSessionAsync(administratorId, workerId);
		var authCookie = await client.SignInAsync("cost.zoned-asof-viewer");

		var response = await client.GetAuthenticatedAsync($"/Jobs/CostReport?nodeId={leafId.Value}&AsOf=2026-01-01T08:00", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(">&#xA3;120.00<");
	}

	/// <summary>
	///     A leaf costed against a &#163;0/hour rate has real allocated time but an exact and displayed
	///     cost of &#163;0.00; the report shows a dash rather than a zero amount that reads as "nothing
	///     recorded" (the same seam as <c>MoneyDisplay</c>/<c>CostDisplay.FormatCell</c> in the Browse
	///     and Awaiting Progress tables — Browse's single-node record card states the figure instead,
	///     via <c>CostDisplay.FormatField</c>).
	/// </summary>
	[Fact]
	public async Task A_leaf_costed_at_a_zero_rate_shows_a_dash_instead_of_zero_amounts()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.zero-rate-worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.zero-rate-viewer", EmployeeRole.CostViewer);
		var leafId = await SeedLeafWithCostedSessionAsync(administratorId, workerId, 0m);
		var authCookie = await client.SignInAsync("cost.zero-rate-viewer");

		var response = await client.GetAuthenticatedAsync($"/Jobs/CostReport?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("&#xA3;0.00");
		body.Should().Contain(">2.0 hrs<");
		body.Should().Contain(">-<");
	}

	private async Task<JobNodeId> SeedLeafWithCostedSessionAsync(
		AppUserId administratorId, AppUserId workerId, decimal hourlyRate = 60m)
	{
		var branch = await seedClient.Jobs.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = rootJobNodeId,
			Description = "Cost report branch",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = branch.Id,
			Description = "Cost report leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = ContextFor(administratorId),
			JobNodeId = leaf.Id,
		});

		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime, new(SessionStart, SessionFinish.Plus(Duration.FromHours(1))), null),
			Reason = "Full working window for cost-report tests",
		});
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Rate = new(new(hourlyRate), Instant.FromUtc(2000, 1, 1, 0, 0), null),
		});

		var session = await seedClient.Work.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.CorrectSessionAsync(new() {
			Context = ContextFor(administratorId),
			SessionId = session.Id,
			StartedAt = SessionStart,
			FinishedAt = SessionFinish,
			Reason = "Pin to a deterministic instant for cost-report tests",
			Version = session.Version,
		});

		return leaf.Id;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	[GeneratedRegex(">&#xA3;120\\.00<")]
	private static partial Regex SterlingAmountPattern();

	private async Task<AppUserId> SeedAdministratorAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT app_user_id
							  FROM identity_user
							  WHERE user_name = 'admin.cost-tests';
							  """;
		var appUserId = (long?)await command.ExecuteScalarAsync()
						?? throw new InvalidOperationException("Bootstrap administrator was not found.");

		return new(appUserId);
	}





	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = AntiforgeryTokenValue(body) ?? throw new InvalidOperationException("No antiforgery token in login page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private static string? AntiforgeryTokenValue(string body)
	{
		const string marker = "name=\"__RequestVerificationToken\"";
		var markerIndex = body.IndexOf(marker, StringComparison.Ordinal);
		if (markerIndex < 0) {
			return null;
		}

		var valueMarker = "value=\"";
		var valueIndex = body.IndexOf(valueMarker, markerIndex, StringComparison.Ordinal);
		if (valueIndex < 0) {
			return null;
		}

		var start = valueIndex + valueMarker.Length;
		var end = body.IndexOf('"', start);
		return end < 0 ? null : body[start..end];
	}
}
