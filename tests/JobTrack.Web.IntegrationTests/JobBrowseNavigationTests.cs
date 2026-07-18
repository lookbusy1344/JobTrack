namespace JobTrack.Web.IntegrationTests;

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
///     Direct-HTTP tests for job-tree navigation on <c>/Jobs/Browse</c>: the breadcrumb path to root,
///     the prerequisite/dependent link lists, and the same links carried onto
///     <c>/Jobs/Prerequisites</c>.
/// </summary>
public sealed partial class JobBrowseNavigationTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId adminId;
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
			UserName = "admin.browse-nav",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrap.RootJobNodeId;
		adminId = bootstrap.AdministratorId;
		await SeedWorkerEmployeeAsync("browse-nav.worker");

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
	public async Task Browsing_the_root_shows_a_breadcrumb_with_no_ancestor_links()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("aria-label=\"breadcrumb\"");
		body.Should().NotContain($"href=\"/Jobs/Browse?nodeId={rootId.Value}\"");
	}

	[Fact]
	public async Task The_owner_filter_renders_as_a_named_dropdown_not_a_numeric_input()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		// The owner filter is a <select> of employee names defaulting to "All owners" (no filter),
		// never a bare numeric id <input> (every filter shows names, not raw AppUserId values).
		body.Should().MatchRegex("<select[^>]*id=\"OwnerUserId\"");
		body.Should().NotMatchRegex("<input[^>]*id=\"OwnerUserId\"");
		body.Should().Contain("All owners");
		body.Should().Contain("browse-nav.worker (browse-nav.worker)");
	}

	[Fact]
	public async Task Browsing_a_direct_child_of_root_shows_a_breadcrumb_link_to_root()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Browse?nodeId={branchId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("aria-label=\"breadcrumb\"");
		body.Should().Contain("href=\"/Jobs/Browse\"");
		body.Should().NotContain($"href=\"/Jobs/Browse?nodeId={branchId.Value}\"");
	}

	[Fact]
	public async Task Browsing_a_grandchild_shows_breadcrumb_links_to_root_and_its_immediate_parent()
	{
		var branchId = await AddChildAsync(rootId, "Kitchen renovation");
		var leafId = await AddChildAsync(branchId, "Fit cabinets");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("href=\"/Jobs/Browse\"");
		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={branchId.Value}\"");
		body.Should().Contain("Kitchen renovation");
	}

	[Fact]
	public async Task Browsing_the_root_hides_prerequisite_and_work_sections()
	{
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync("/Jobs/Browse", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().NotContain("Requires (must finish first)");
		body.Should().NotContain("Depends on this job");
		body.Should().NotContain(">Dependencies<");
		body.Should().NotContain(">Decompose<");
		body.Should().NotContain(">Work<");
		body.Should().NotContain(">Achievement<");
	}

	[Fact]
	public async Task Browsing_a_leaf_still_shows_prerequisite_and_work_sections()
	{
		var leafId = await AddChildAsync(rootId, "Pour foundation");
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Browse?nodeId={leafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain("Requires (must finish first)");
		body.Should().Contain("Depends on this job");
		body.Should().Contain(">Dependencies<");
		body.Should().Contain(">Decompose<");
		body.Should().Contain(">Start work<");
		body.Should().Contain(">Achievement<");
	}

	[Fact]
	public async Task Prerequisite_and_dependent_links_render_with_descriptions_including_an_archived_node()
	{
		var requiredLeafId = await AddChildAsync(rootId, "Old survey");
		var dependentLeafId = await AddChildAsync(rootId, "Frame walls");
		await ArchiveAsync(requiredLeafId);
		await AddPrerequisiteAsync(requiredLeafId, dependentLeafId);
		var authCookie = await SignInAsync("browse-nav.worker");

		var dependentResponse = await GetAsync($"/Jobs/Browse?nodeId={dependentLeafId.Value}", authCookie);
		var dependentBody = await ReadNormalizedBodyAsync(dependentResponse);
		dependentBody.Should().Contain($"href=\"/Jobs/Browse?nodeId={requiredLeafId.Value}\"");
		dependentBody.Should().Contain("Old survey");

		var requiredResponse = await GetAsync($"/Jobs/Browse?nodeId={requiredLeafId.Value}", authCookie);
		var requiredBody = await ReadNormalizedBodyAsync(requiredResponse);
		requiredBody.Should().Contain($"href=\"/Jobs/Browse?nodeId={dependentLeafId.Value}\"");
		requiredBody.Should().Contain("Frame walls");
	}

	[Fact]
	public async Task Prerequisites_page_links_to_the_related_job_by_description_instead_of_a_bare_id()
	{
		var requiredLeafId = await AddChildAsync(rootId, "Pour foundation");
		var dependentLeafId = await AddChildAsync(rootId, "Frame walls");
		await AddPrerequisiteAsync(requiredLeafId, dependentLeafId);
		var authCookie = await SignInAsync("browse-nav.worker");

		var response = await GetAsync($"/Jobs/Prerequisites?nodeId={dependentLeafId.Value}", authCookie);
		var body = await ReadNormalizedBodyAsync(response);

		body.Should().Contain($"href=\"/Jobs/Browse?nodeId={requiredLeafId.Value}\"");
		body.Should().Contain("Pour foundation");
		body.Should().NotContain($"Job {requiredLeafId.Value}</span>");
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = adminId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task ArchiveAsync(JobNodeId nodeId)
	{
		var node = await seedClient.Query.GetJobNodeAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			NodeId = nodeId,
		});

		_ = await seedClient.Jobs.ArchiveAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			NodeId = nodeId,
			Version = node.Node.Version,
		});
	}

	private async Task AddPrerequisiteAsync(JobNodeId requiredJobId, JobNodeId dependentJobId) =>
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = requiredJobId,
			DependentJobId = dependentJobId,
		});

	private static async Task<string> ReadNormalizedBodyAsync(HttpResponseMessage response) =>
		WhitespaceRunPattern().Replace(await response.Content.ReadAsStringAsync(), " ");

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

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRunPattern();

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

	private async Task SeedWorkerEmployeeAsync(string userName)
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
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)EmployeeRole.Worker);
		_ = await insertRole.ExecuteNonQueryAsync();
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
