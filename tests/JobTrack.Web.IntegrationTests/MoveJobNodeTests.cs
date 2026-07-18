namespace JobTrack.Web.IntegrationTests;

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
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the job-node move workflow (plan §8.5 slice 3), plus recovery from a stale
///     <see cref="ConcurrencyConflictException" /> and from an <see cref="InvariantViolationException" />
///     when the chosen destination is the node's own descendant (a hierarchy cycle).
/// </summary>
public sealed partial class MoveJobNodeTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

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
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.move-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;
		administratorId = bootstrapResult.AdministratorId;

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
	public async Task A_job_manager_can_save_moving_a_branch_to_a_new_parent()
	{
		var managerId = await SeedEmployeeAsync("move.manager", EmployeeRole.JobManager);
		var sourceBranch = await AddChildAsync(rootId, managerId, "Source branch");
		var moved = await AddChildAsync(sourceBranch.Id, managerId, "Pour foundation");
		var destinationBranch = await AddChildAsync(rootId, managerId, "Destination branch");
		var authCookie = await SignInAsync("move.manager");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, moved.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, moved.Id, moved.Version, destinationBranch.Id);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterSave = await GetAsync($"/Jobs/Browse?nodeId={destinationBranch.Id.Value}", authCookie);
		(await afterSave.Content.ReadAsStringAsync()).Should().Contain("Pour foundation");

		var oldParent = await GetAsync($"/Jobs/Browse?nodeId={sourceBranch.Id.Value}", authCookie);
		(await oldParent.Content.ReadAsStringAsync()).Should().NotContain("Pour foundation");
	}

	[Fact]
	public async Task The_move_page_offers_save_and_cancel_actions()
	{
		var managerId = await SeedEmployeeAsync("move.actions", EmployeeRole.JobManager);
		var moved = await AddChildAsync(rootId, managerId, "Movable branch");
		var authCookie = await SignInAsync("move.actions");

		var response = await GetAsync($"/Jobs/Move?nodeId={moved.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(">Save<");
		body.Should().Contain(">Cancel<");
		body.Should().NotContain("Preview");
		body.Should().NotContain("Confirm move");
	}

	[Fact]
	public async Task A_worker_who_does_not_own_the_node_is_denied_on_save()
	{
		var managerId = await SeedEmployeeAsync("move.owner-manager", EmployeeRole.JobManager);
		var workerId = await SeedEmployeeAsync("move.denied-worker", EmployeeRole.Worker);
		var moved = await AddChildAsync(rootId, managerId, "Owned by manager");
		var destination = await AddChildAsync(rootId, managerId, "Destination");
		var authCookie = await SignInAsync("move.denied-worker");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, moved.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, moved.Id, moved.Version, destination.Id);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task Moving_a_branch_under_its_own_descendant_is_reported_as_a_cycle()
	{
		var managerId = await SeedEmployeeAsync("move.cycle-manager", EmployeeRole.JobManager);
		var branch = await AddChildAsync(rootId, managerId, "Ancestor branch");
		var descendant = await AddChildAsync(branch.Id, managerId, "Descendant branch");
		var authCookie = await SignInAsync("move.cycle-manager");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, branch.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, branch.Id, branch.Version, descendant.Id);
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("cycle");
	}

	[Fact]
	public async Task A_stale_version_on_save_is_reported_as_a_conflict()
	{
		var managerId = await SeedEmployeeAsync("move.conflict-manager", EmployeeRole.JobManager);
		var moved = await AddChildAsync(rootId, managerId, "Contested leaf");
		var destination = await AddChildAsync(rootId, managerId, "Destination");
		var authCookie = await SignInAsync("move.conflict-manager");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, moved.Id);

		// A concurrent edit lands after the form was loaded, advancing the row's version.
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = moved.Id,
			Description = "Concurrently edited",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
			Version = moved.Version,
		});

		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, moved.Id, moved.Version, destination.Id);
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("Someone else changed this node");
	}

	private async Task<JobNodeResult> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description) =>
		await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

	private async Task<HttpResponseMessage> PostAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId nodeId, long version, JobNodeId newParentId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Move");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["NodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["OriginalVersion"] = version.ToString(CultureInfo.InvariantCulture),
			["Input.NewParentId"] = newParentId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetMoveFormAsync(string authCookie, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Move?nodeId={nodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Move page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Move page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
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
