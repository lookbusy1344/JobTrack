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
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the job-node delete workflow (ADR 0036): an unused leaf deletes outright,
///     a node with children is never offered the form, a non-administrator is denied deleting a worked
///     leaf with a friendly message rather than a raw 403, and an administrator can force-delete a
///     worked leaf only when they supply a reason.
/// </summary>
public sealed partial class DeleteJobNodeTests : IAsyncLifetime, IDisposable
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
			UserName = "admin.delete-tests",
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
	public async Task A_job_manager_can_delete_an_unused_leaf()
	{
		var managerId = await SeedEmployeeAsync("delete.manager", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Unused leaf");
		var authCookie = await SignInAsync("delete.manager");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterDelete = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await afterDelete.Content.ReadAsStringAsync()).Should().NotContain("Unused leaf");
	}

	[Fact]
	public async Task A_leaf_with_unused_leaf_work_deletes_along_with_it()
	{
		var managerId = await SeedEmployeeAsync("delete.unused-leafwork-manager", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Attached but never worked");
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});
		var authCookie = await SignInAsync("delete.unused-leafwork-manager");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");
	}

	[Fact]
	public async Task A_node_with_children_is_never_offered_the_delete_form()
	{
		var managerId = await SeedEmployeeAsync("delete.parent-manager", EmployeeRole.JobManager);
		var parent = await AddChildAsync(rootId, managerId, "Parent with a child");
		_ = await AddChildAsync(parent.Id, managerId, "Child");
		var authCookie = await SignInAsync("delete.parent-manager");

		var response = await GetAsync($"/Jobs/Delete?nodeId={parent.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("cannot be deleted");
		body.Should().NotContain("Delete permanently");
	}

	[Fact]
	public async Task A_non_administrator_is_denied_deleting_a_worked_leaf_with_a_friendly_message()
	{
		var managerId = await SeedEmployeeAsync("delete.denied-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(managerId, "Worked leaf, denied");
		var authCookie = await SignInAsync("delete.denied-manager");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "Trying anyway.");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("requires the Administrator role");
	}

	[Fact]
	public async Task An_administrator_deleting_a_worked_leaf_without_a_reason_is_prompted_for_one()
	{
		var adminId = await SeedEmployeeAsync("delete.admin-no-reason", EmployeeRole.Administrator);
		var leaf = await AddWorkedLeafAsync(adminId, "Worked leaf, no reason yet");
		var authCookie = await SignInAsync("delete.admin-no-reason");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("requires a reason");
	}

	[Fact]
	public async Task An_administrator_can_delete_a_worked_leaf_with_a_reason()
	{
		var adminId = await SeedEmployeeAsync("delete.admin-with-reason", EmployeeRole.Administrator);
		var leaf = await AddWorkedLeafAsync(adminId, "Worked leaf, deleted with reason");
		var authCookie = await SignInAsync("delete.admin-with-reason");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "Duplicate of another job; created and worked in error.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");
	}

	private async Task<JobNodeResult> AddWorkedLeafAsync(AppUserId ownerId, string description)
	{
		var leaf = await AddChildAsync(rootId, ownerId, description);
		var context = new CommandContext { Actor = administratorId, CorrelationId = Guid.NewGuid() };
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() { Context = context, JobNodeId = leaf.Id });
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = context,
			LeafWorkId = leaf.Id,
			WorkedByUserId = ownerId,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = context,
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = Instant.FromUtc(2026, 1, 1, 10, 0),
		});

		// Re-read so the caller sees the leaf's post-attach version, not the pre-attach one.
		var refreshed = await seedClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = leaf.Id });
		return leaf with { Version = refreshed.Node.Version };
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
		string authCookie, string antiforgeryCookie, string token, JobNodeId nodeId, long version, string? reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Delete");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var formValues = new Dictionary<string, string> {
			["NodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["OriginalVersion"] = version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (reason is not null) {
			formValues["Input.Reason"] = reason;
		}

		request.Content = new FormUrlEncodedContent(formValues);

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetDeleteFormAsync(string authCookie, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Delete?nodeId={nodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Delete page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Delete page body.");

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
