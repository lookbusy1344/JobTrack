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
///     Direct-HTTP tests for the job-node create workflow (plan §8.5 slice 3).
///     <see cref="Domain.Authorization.JobNodeAccessPolicy" /> is re-evaluated by the command itself, so
///     an unauthorized actor is denied only at save time (plan §8.3, TC-WEB-AUTHZ-001-style coverage).
/// </summary>
public sealed partial class CreateJobNodeTests : IAsyncLifetime, IDisposable
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
			UserName = "admin.create-tests",
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
	public async Task A_job_manager_can_save_a_new_child_under_the_root()
	{
		var managerId = await SeedEmployeeAsync("create.manager", EmployeeRole.JobManager);
		var authCookie = await SignInAsync("create.manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var beforeSave = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await beforeSave.Content.ReadAsStringAsync()).Should().NotContain("Pour foundation");

		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, "Pour foundation", managerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterSave = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await afterSave.Content.ReadAsStringAsync()).Should().Contain("Pour foundation");
	}

	[Fact]
	public async Task The_create_page_has_no_branch_or_leaf_selector()
	{
		_ = await SeedEmployeeAsync("create.no-kind", EmployeeRole.JobManager);
		var authCookie = await SignInAsync("create.no-kind");

		var response = await GetAsync($"/Jobs/Create?parentId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("Create child");
		body.Should().Contain(">Save<");
		body.Should().Contain(">Cancel<");
		body.Should().NotContain("name=\"Kind\"");
		body.Should().NotContain("New branch");
		body.Should().NotContain("New leaf");
		body.Should().NotContain("Preview");
	}

	/// <summary>
	///     TC-WEB-AUTHN-007 (threat model row 5): a description containing script markup is
	///     rendered HTML-encoded when browsing the created node, not as live markup, proving Razor's
	///     default output encoding holds for user-supplied job-tree content.
	/// </summary>
	[Fact]
	public async Task A_description_containing_script_markup_is_rendered_html_encoded_not_as_live_markup()
	{
		const string InjectedDescription = "<script>alert('xss')</script>";
		var managerId = await SeedEmployeeAsync("create.xss-manager", EmployeeRole.JobManager);
		var authCookie = await SignInAsync("create.xss-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, InjectedDescription, managerId);
		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var browseResponse = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();

		browseBody.Should().NotContain(InjectedDescription);
		browseBody.Should().Contain("&lt;script&gt;alert(&#x27;xss&#x27;)&lt;/script&gt;");
	}

	[Fact]
	public async Task A_worker_who_does_not_own_the_parent_is_denied_on_save()
	{
		var workerId = await SeedEmployeeAsync("create.denied-worker", EmployeeRole.Worker);
		var authCookie = await SignInAsync("create.denied-worker");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, "Unauthorized child", workerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_worker_who_owns_the_parent_can_create_a_child_under_it()
	{
		var workerId = await SeedEmployeeAsync("create.owning-worker", EmployeeRole.Worker);
		var branchResult = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Worker-owned branch",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		var authCookie = await SignInAsync("create.owning-worker");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, branchResult.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, branchResult.Id, "Owned child", workerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");
	}

	[Fact]
	public async Task A_job_manager_can_create_an_unassigned_child_from_a_blank_owner_field()
	{
		var managerId = await SeedEmployeeAsync("create.unassigned-manager", EmployeeRole.JobManager);
		var authCookie = await SignInAsync("create.unassigned-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, "Pool child from web", null);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var browseResponse = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}&unassignedOnly=true", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();
		browseBody.Should().Contain("Pool child from web");
	}

	[Fact]
	public async Task Creating_under_a_nonexistent_parent_shows_an_error()
	{
		var managerId = await SeedEmployeeAsync("create.missing-parent", EmployeeRole.JobManager);
		var authCookie = await SignInAsync("create.missing-parent");
		var missingParentId = new JobNodeId(rootId.Value + 999);

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, missingParentId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, missingParentId, "Orphan child", managerId);
		var body = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("does not exist");
	}

	[Fact]
	public async Task Creating_under_a_parent_that_already_has_leaf_work_shows_an_error()
	{
		var managerId = await SeedEmployeeAsync("create.worked-parent-manager", EmployeeRole.JobManager);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Worked parent",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = managerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});
		var authCookie = await SignInAsync("create.worked-parent-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, "Invalid child", managerId);
		var body = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("already has work attached");
	}

	private async Task<HttpResponseMessage> PostAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId parentId, string description, AppUserId? ownerId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Create");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["ParentId"] = parentId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.Description"] = description,
			["Input.OwnerUserId"] = ownerId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
			["Input.Priority"] = nameof(Priority.Medium),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetCreateFormAsync(string authCookie, JobNodeId parentId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Create?parentId={parentId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Create page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Create page body.");

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
