namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Domain.Rates;
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
		await DeploySchemaAsync();

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
	public async Task A_cost_viewer_sees_rate_provenance_and_readiness_for_a_leaf()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await SeedEmployeeAsync("cost.worker", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("cost.viewer", EmployeeRole.CostViewer);
		var leafId = await SeedLeafWithCostedSessionAsync(administratorId, workerId);
		var authCookie = await SignInAsync("cost.viewer");

		var response = await GetAsync($"/Jobs/CostReport?nodeId={leafId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		SterlingAmountPattern().Count(body).Should().Be(3);
		body.Should().Contain(">&#xA3;120.00<");
		body.Should().Contain(nameof(RateSource.UserCostRate));
		body.Should().Contain("Ready");
	}

	[Fact]
	public async Task A_worker_cannot_open_the_cost_report()
	{
		var administratorId = await SeedAdministratorAsync();
		var workerId = await SeedEmployeeAsync("cost.self", EmployeeRole.Worker);
		var leafId = await SeedLeafWithCostedSessionAsync(administratorId, workerId);
		var authCookie = await SignInAsync("cost.self");

		var response = await GetAsync($"/Jobs/CostReport?nodeId={leafId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task Requesting_a_nonexistent_node_shows_an_error_instead_of_failing()
	{
		_ = await SeedEmployeeAsync("cost.viewer-missing", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("cost.viewer-missing");

		var response = await GetAsync("/Jobs/CostReport?nodeId=999999", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("does not exist");
	}

	private async Task<JobNodeId> SeedLeafWithCostedSessionAsync(AppUserId administratorId, AppUserId workerId)
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
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() { Context = ContextFor(administratorId), JobNodeId = leaf.Id });

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
			Rate = new(new(60m), Instant.FromUtc(2000, 1, 1, 0, 0), null),
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

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

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
		var token = AntiforgeryTokenValue(body) ?? throw new InvalidOperationException("No antiforgery token in login page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
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

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

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
