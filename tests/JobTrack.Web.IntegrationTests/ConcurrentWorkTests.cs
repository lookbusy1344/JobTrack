namespace JobTrack.Web.IntegrationTests;

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
///     Direct-HTTP tests for <c>/Jobs/ConcurrentWork</c>: which other jobs a leaf's own workers were
///     clocked on to at the same time, grouped by worker. No per-role policy beyond the baseline
///     employee one, matching Browse and the sessions panel — recorded work is job data every
///     employee role may read (ADR 0041) — and no cost is rendered, so no rate gate applies.
/// </summary>
public sealed partial class ConcurrentWorkTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();

	/// <summary>
	///     One instant captured per test class, so two seeded sessions an hour apart really are an hour
	///     apart: calling the clock once per boundary would shift each by the milliseconds between calls
	///     and turn an exact hour of overlap into "59m".
	/// </summary>
	private readonly Instant seedNow = SystemClock.Instance.GetCurrentInstant();

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
	public async Task An_overlapping_job_is_listed_under_the_worker_who_worked_both()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.basic");
		var rootId = bootstrappedRootId!.Value;
		var subject = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var other = await AddLeafWithWorkAsync(rootId, workerId, "Fit worktop", adminId);
		await AddFinishedSessionAsync(workerId, subject.JobNodeId, HoursAgo(5), HoursAgo(2));
		await AddFinishedSessionAsync(workerId, other.JobNodeId, HoursAgo(3), HoursAgo(1));
		var authCookie = await client.SignInAsync("concurrent.basic");

		var response = await client.GetAuthenticatedAsync($"/Jobs/ConcurrentWork?nodeId={subject.JobNodeId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit worktop");
		body.Should().Contain("concurrent.basic", "each block is headed by the worker who was clocked on to both jobs");
		body.Should().Contain("1h 0m", "the two sessions share exactly the hour between them");
	}

	[Fact]
	public async Task A_listed_job_leads_with_its_kind_glyph_rather_than_spending_a_column_on_it()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.kind");
		var rootId = bootstrappedRootId!.Value;
		var subject = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var other = await AddLeafWithWorkAsync(rootId, workerId, "Fit worktop", adminId);
		await AddFinishedSessionAsync(workerId, subject.JobNodeId, HoursAgo(5), HoursAgo(2));
		await AddFinishedSessionAsync(workerId, other.JobNodeId, HoursAgo(3), HoursAgo(1));
		var authCookie = await client.SignInAsync("concurrent.kind");

		var response = await client.GetAuthenticatedAsync($"/Jobs/ConcurrentWork?nodeId={subject.JobNodeId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain(">Kind</th>", "the kind is a glyph on the job, not a column of its own");
		body.Should().MatchRegex(
			KindGlyphBeforeJobNamePattern(),
			"the glyph leads the job's own name, as it does on Browse's tree rows");
		body.Should().Contain(">Sess</th>", "the session count's heading is abbreviated so its column stays narrow");
	}

	/// <summary>
	///     An overlap that has not ended yet is clipped to the report's own asOf, so rendering that end
	///     as a timestamp claims a closed window that happened to finish this minute — and the claim goes
	///     stale the moment the page is read. "now" says the true thing, that both jobs are still running
	///     together.
	/// </summary>
	[Fact]
	public async Task An_overlap_that_is_still_running_ends_at_now_rather_than_at_a_timestamp()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.running");
		var rootId = bootstrappedRootId!.Value;
		var subject = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var other = await AddLeafWithWorkAsync(rootId, workerId, "Fit worktop", adminId);
		await AddActiveSessionAsync(workerId, subject.JobNodeId, HoursAgo(2));
		await AddActiveSessionAsync(workerId, other.JobNodeId, HoursAgo(1));
		var authCookie = await client.SignInAsync("concurrent.running");

		var response = await client.GetAuthenticatedAsync($"/Jobs/ConcurrentWork?nodeId={subject.JobNodeId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit worktop");
		body.Should().MatchRegex("&ndash;\\s*now", "the open end of a running overlap reads as now, not as the instant the page happened to load");
	}

	[Fact]
	public async Task A_finished_overlap_still_names_the_instant_it_ended()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.finished");
		var rootId = bootstrappedRootId!.Value;
		var subject = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var other = await AddLeafWithWorkAsync(rootId, workerId, "Fit worktop", adminId);
		await AddFinishedSessionAsync(workerId, subject.JobNodeId, HoursAgo(5), HoursAgo(2));
		await AddFinishedSessionAsync(workerId, other.JobNodeId, HoursAgo(3), HoursAgo(1));
		var authCookie = await client.SignInAsync("concurrent.finished");

		var response = await client.GetAuthenticatedAsync($"/Jobs/ConcurrentWork?nodeId={subject.JobNodeId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotMatchRegex("&ndash;\\s*now", "a closed overlap ended at a real instant, and that is what it must report");
	}

	[Fact]
	public async Task A_job_worked_at_a_different_time_is_not_listed()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.disjoint");
		var rootId = bootstrappedRootId!.Value;
		var subject = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var other = await AddLeafWithWorkAsync(rootId, workerId, "Fit worktop", adminId);
		await AddFinishedSessionAsync(workerId, subject.JobNodeId, HoursAgo(5), HoursAgo(4));
		await AddFinishedSessionAsync(workerId, other.JobNodeId, HoursAgo(3), HoursAgo(2));
		var authCookie = await client.SignInAsync("concurrent.disjoint");

		var response = await client.GetAuthenticatedAsync($"/Jobs/ConcurrentWork?nodeId={subject.JobNodeId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("Fit worktop");
		body.Should().Contain("Nobody worked another job while working this one.");
	}

	[Fact]
	public async Task A_branch_is_refused_rather_than_shown_as_an_empty_report()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.branch");
		var rootId = bootstrappedRootId!.Value;
		var branch = await AddChildAsync(rootId, workerId, "Kitchen", adminId);
		_ = await AddLeafWithWorkAsync(branch, workerId, "Install cabinets", adminId);
		var authCookie = await client.SignInAsync("concurrent.branch");

		var response = await client.GetAuthenticatedAsync($"/Jobs/ConcurrentWork?nodeId={branch.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Concurrent work is reported for a leaf job only.");
	}

	[Fact]
	public async Task Browse_links_a_leaf_to_its_concurrent_work_and_a_branch_does_not()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.link");
		var rootId = bootstrappedRootId!.Value;
		var branch = await AddChildAsync(rootId, workerId, "Kitchen", adminId);
		var leaf = await AddLeafWithWorkAsync(branch, workerId, "Install cabinets", adminId);
		var authCookie = await client.SignInAsync("concurrent.link");

		var leafBrowse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leaf.JobNodeId.Value}", authCookie);
		var leafBody = await leafBrowse.Content.ReadAsStringAsync();
		var branchBrowse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={branch.Value}", authCookie);
		var branchBody = await branchBrowse.Content.ReadAsStringAsync();

		leafBody.Should().Contain(
			$"</span> <a class=\"jt-value-aside\" href=\"/Jobs/ConcurrentWork?nodeId={leaf.JobNodeId.Value}\">Info</a>",
			"the link reads as a qualifier on the cost figure it explains, not as a field of its own");
		branchBody.Should().NotContain("/Jobs/ConcurrentWork");
	}

	[Fact]
	public async Task Another_workers_job_at_the_same_time_is_not_concurrent_work()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.otherworker");
		var otherWorkerId = await SeedWorkerEmployeeAsync("concurrent.otherworker.mate");
		var rootId = bootstrappedRootId!.Value;
		var subject = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var other = await AddLeafWithWorkAsync(rootId, otherWorkerId, "Fit worktop", adminId);
		await AddFinishedSessionAsync(workerId, subject.JobNodeId, HoursAgo(5), HoursAgo(2));
		await AddFinishedSessionAsync(otherWorkerId, other.JobNodeId, HoursAgo(5), HoursAgo(2));
		var authCookie = await client.SignInAsync("concurrent.otherworker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/ConcurrentWork?nodeId={subject.JobNodeId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain("Fit worktop", "concurrency is per worker -- two people working at once share no allocation");
	}

	[Fact]
	public async Task A_signed_out_visitor_is_challenged()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("concurrent.anon");
		var leaf = await AddLeafWithWorkAsync(bootstrappedRootId!.Value, workerId, "Install cabinets", adminId);

		var response = await client.GetAsync($"/Jobs/ConcurrentWork?nodeId={leaf.JobNodeId.Value}");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	private Instant HoursAgo(int hours) => seedNow - Duration.FromHours(hours);

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
		var node = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return node.Id;
	}

	private async Task<LeafWorkResult> AddLeafWithWorkAsync(JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId)
	{
		var leafId = await AddChildAsync(parentId, ownerId, description, adminId);

		return await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() }, JobNodeId = leafId });
	}

	private async Task AddActiveSessionAsync(AppUserId workerId, JobNodeId leafId, Instant startedAt) =>
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = startedAt,
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







	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	/// <summary>
	///     The kind glyph immediately before the job's own link in a row's first cell. Singleline
	///     because the partial renders its <c>svg</c> across several lines between the two.
	/// </summary>
	[GeneratedRegex("<span class=\"jt-kind-icon\">.*?</span>\\s*<a class=\"jt-preserve-whitespace\"", RegexOptions.Singleline)]
	private static partial Regex KindGlyphBeforeJobNamePattern();

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
		var deployer = new SchemaDeployer(
			connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

}
