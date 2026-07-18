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
///     Direct-HTTP tests for the flat "jobs awaiting progress" dashboard: leaves only, filtered by
///     owner and/or subtree, in priority/deadline order. No per-role authorization policy, matching
///     <c>JobTreeBrowsingTests</c> — viewing job data is an unqualified baseline capability for every
///     role (spec §7.3).
/// </summary>
public sealed partial class AwaitingProgressTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId? bootstrappedAdminId;
	private JobNodeId? bootstrappedRootId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);

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
	public async Task A_waiting_leaf_appears_and_a_succeeded_leaf_does_not()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.basic");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var doneLeaf = await AddLeafWithWorkAsync(rootId, workerId, "Painting", adminId);
		await SetAchievementAsync(doneLeaf.JobNodeId, Achievement.InProgress, adminId, doneLeaf.Version);
		var inProgress = await seedClient.Query.GetLeafWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = doneLeaf.JobNodeId,
		});
		await SetAchievementAsync(doneLeaf.JobNodeId, Achievement.Success, adminId, inProgress.Version);
		var authCookie = await SignInAsync("awaiting.basic");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Painting");
	}

	[Fact]
	public async Task A_leaf_with_no_leaf_work_attached_appears_on_the_dashboard()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.noleafwork");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddChildAsync(rootId, workerId, "Fresh leaf awaiting assignment", adminId);
		var authCookie = await SignInAsync("awaiting.noleafwork");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fresh leaf awaiting assignment");
		body.Should().Contain("No work attached");
	}

	[Fact]
	public async Task A_leaf_blocked_by_an_unsatisfied_prerequisite_still_appears_marked_blocked()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.blocked");
		var rootId = bootstrappedRootId!.Value;
		var required = await AddLeafWithWorkAsync(rootId, workerId, "Required first", adminId);
		var dependent = await AddLeafWithWorkAsync(rootId, workerId, "Blocked leaf", adminId);
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = required.JobNodeId,
			DependentJobId = dependent.JobNodeId,
		});
		var authCookie = await SignInAsync("awaiting.blocked");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Blocked leaf");
		body.Should().Contain("Blocked");
	}

	[Fact]
	public async Task Starting_work_from_a_dashboard_row_advances_the_leaf_to_in_progress()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.startwork");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Fresh leaf via dashboard", adminId);
		var authCookie = await SignInAsync("awaiting.startwork");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Work started");
		body.Should().Contain("Fresh leaf via dashboard");
		body.Should().Contain("In Progress");
	}

	[Fact]
	public async Task Filtering_by_owner_hides_another_employees_leaf()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.owner");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Worker job", adminId);
		_ = await AddLeafWithWorkAsync(rootId, adminId, "Admin job", adminId);
		var authCookie = await SignInAsync("awaiting.owner");

		var response = await GetAsync($"/Jobs/AwaitingProgress?ownerUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker job");
		body.Should().NotContain("Admin job");
	}

	[Fact]
	public async Task Scoping_to_a_subtree_hides_a_leaf_outside_it()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.subtree");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Outside the branch", adminId);
		var authCookie = await SignInAsync("awaiting.subtree");

		var response = await GetAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Outside the branch");
	}

	[Fact]
	public async Task A_leaf_with_an_active_session_shows_a_finish_button_instead_of_start()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.toggle");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Toggle leaf", adminId);
		var authCookie = await SignInAsync("awaiting.toggle");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, formCookie, token, leafId);
		var startBody = await startResponse.Content.ReadAsStringAsync();

		startResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		startBody.Should().Contain("Finish / pause");
		startBody.Should().NotContain(">Start work<");
	}

	[Fact]
	public async Task Finishing_work_from_the_dashboard_returns_the_row_to_a_start_button()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.finish");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Finish leaf", adminId);
		var authCookie = await SignInAsync("awaiting.finish");

		var (startFormCookie, startToken) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, startFormCookie, startToken, leafId);
		var startBody = await startResponse.Content.ReadAsStringAsync();
		var (sessionId, version) = ExtractFirstSession(startBody);

		var (finishCookie, finishToken) = await ExtractFormAsync(startResponse, startFormCookie);
		var finishResponse = await PostFinishWorkAsync(authCookie, finishCookie, finishToken, sessionId, version);
		var finishBody = await finishResponse.Content.ReadAsStringAsync();

		finishResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		finishBody.Should().Contain("Session finished");
		finishBody.Should().Contain(">Start work<");
		finishBody.Should().NotContain("Finish / pause");
	}

	[Fact]
	public async Task The_dashboard_shows_current_costs_for_paused_and_in_progress_jobs()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.costs");
		_ = await SeedWorkerEmployeeAsync("awaiting.costs.viewer", EmployeeRole.CostViewer);
		var rootId = bootstrappedRootId!.Value;
		var pausedLeaf = await AddChildAsync(rootId, workerId, "Paused costed leaf", adminId);
		var activeLeaf = await AddChildAsync(rootId, workerId, "Active costed leaf", adminId);
		await AttachLeafWorkAsync(pausedLeaf, adminId);
		await AttachLeafWorkAsync(activeLeaf, adminId);

		var now = SystemClock.Instance.GetCurrentInstant();
		await AddWorkingWindowAsync(workerId, adminId, now - Duration.FromDays(1), now - Duration.FromDays(1) + Duration.FromHours(9));
		await AddWorkingWindowAsync(workerId, adminId, now - Duration.FromHours(2), now + Duration.FromHours(1));
		await AddUserCostRateAsync(workerId, adminId, 25m, now - Duration.FromDays(2));
		await AddFinishedSessionAsync(workerId, pausedLeaf, now - Duration.FromDays(1), now - Duration.FromDays(1) + Duration.FromHours(8));
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = activeLeaf,
			WorkedByUserId = workerId,
			StartedAt = now - Duration.FromHours(1),
		});
		var authCookie = await SignInAsync("awaiting.costs.viewer");

		var response = await GetAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Paused costed leaf");
		body.Should().Contain("Active costed leaf");
		body.Should().Contain(">&#xA3;200.00<");
		body.Should().Contain(">&#xA3;25.00<");
	}

	[Fact]
	public async Task An_unauthenticated_request_is_redirected_to_sign_in()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	private async Task<(AppUserId AdministratorId, AppUserId WorkerId)> BootstrapAndSeedWorkerAsync(string workerUserName)
	{
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = $"admin.{workerUserName}",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});

		bootstrappedRootId = bootstrapResult.RootJobNodeId;
		bootstrappedAdminId = bootstrapResult.AdministratorId;

		var workerId = await SeedWorkerEmployeeAsync(workerUserName);

		return (bootstrapResult.AdministratorId, workerId);
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<LeafWorkResult> AddLeafWithWorkAsync(JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id });
	}

	private async Task AttachLeafWorkAsync(JobNodeId leafId, AppUserId adminId) =>
		_ = await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() }, JobNodeId = leafId });

	private async Task AddWorkingWindowAsync(AppUserId workerId, AppUserId adminId, Instant start, Instant end) =>
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(start, end),
				null),
			Reason = "Working window for awaiting-progress cost test",
		});

	private async Task AddUserCostRateAsync(AppUserId workerId, AppUserId adminId, decimal amountPerHour, Instant effectiveStart) =>
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Rate = new(new(amountPerHour), effectiveStart, null),
		});

	private async Task AddFinishedSessionAsync(AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = startedAt,
		});

		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = finishedAt,
		});
	}

	private async Task SetAchievementAsync(JobNodeId leafId, Achievement newAchievement, AppUserId adminId, long version) =>
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			NewAchievement = newAchievement,
			Reason = "Test transition",
			Version = version,
		});

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostStartWorkAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId jobNodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/AwaitingProgress?handler=StartWork");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["jobNodeId"] = jobNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostFinishWorkAsync(string authCookie, string antiforgeryCookie, string token, long sessionId,
		long version)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/AwaitingProgress?handler=Finish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["sessionId"] = sessionId.ToString(CultureInfo.InvariantCulture),
			["version"] = version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
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

	private static (long SessionId, long Version) ExtractFirstSession(string body)
	{
		var sessionIdMatch = SessionIdPattern().Match(body);
		var versionMatch = VersionPattern().Match(body);
		if (!sessionIdMatch.Success || !versionMatch.Success) {
			throw new InvalidOperationException("No session row found in AwaitingProgress page body.");
		}

		return (long.Parse(sessionIdMatch.Groups["id"].Value, CultureInfo.InvariantCulture),
			long.Parse(versionMatch.Groups["version"].Value, CultureInfo.InvariantCulture));
	}

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

	private async Task<AppUserId> SeedWorkerEmployeeAsync(string userName, EmployeeRole role = EmployeeRole.Worker)
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
