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
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the recursive subtree delete/archive page (ADR 0061): only an
///     administrator reaches it, the confirmation renders the whole manifest as a tree with per-node
///     and total cost, deleting removes every descendant, and archiving is the non-destructive
///     alternative that needs no reason. Covers the page's rendered shape as well as its outcomes,
///     since the tree layout and cost columns are the parts a unit test cannot see.
/// </summary>
public sealed partial class DeleteSubtreeTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private readonly ConcurrentBag<string> capturedLogEntries = [];
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
			UserName = "admin.delete-subtree-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;
		administratorId = bootstrapResult.AdministratorId;

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
	public async Task A_non_administrator_cannot_reach_the_subtree_delete_page()
	{
		var managerId = await SeedEmployeeAsync("subtree.manager", EmployeeRole.JobManager);
		var branch = await AddChildAsync(rootId, managerId, "Manager branch");
		_ = await AddChildAsync(branch.Id, managerId, "Manager child");
		var authCookie = await SignInAsync("subtree.manager");

		var response = await GetAsync($"/Jobs/DeleteSubtree?nodeId={branch.Id.Value}", authCookie);

		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
	}

	[Fact]
	public async Task The_confirmation_lists_every_descendant_as_a_tree_with_costs()
	{
		var adminId = await SeedEmployeeAsync("subtree.admin-render", EmployeeRole.Administrator);
		var branch = await AddChildAsync(rootId, adminId, "Doomed branch");
		var child = await AddChildAsync(branch.Id, adminId, "Doomed child");
		_ = await AddChildAsync(child.Id, adminId, "Doomed grandchild");
		var authCookie = await SignInAsync("subtree.admin-render");

		var response = await GetAsync($"/Jobs/DeleteSubtree?nodeId={branch.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("This is destructive and cannot be undone");
		// Named the same "Description (ID n)" way as everywhere else, via the shared JobNodeDisplay
		// helper rather than a per-page truncation.
		body.Should().Contain($"Doomed branch (ID {branch.Id.Value})");
		body.Should().Contain($"Doomed child (ID {child.Id.Value})");
		body.Should().Contain("Doomed grandchild (ID ");

		// The tree layout Browse uses, not a padding utility: depth attributes and guide rails present.
		body.Should().Contain("jt-tree-cell").And.Contain("data-jt-depth=\"2\"");
		body.Should().Contain("jt-tree-icon--branch").And.Contain("jt-tree-icon--leaf");

		// Cost is offered per node and as a subtree total; the retired State and Sessions columns are
		// gone, the session count surviving only as a summary figure above the table.
		body.Should().Contain("Total cost to destroy");
		body.Should().Contain(">Cost</th>");
		body.Should().NotContain(">State</th>");
		body.Should().NotContain(">Sessions</th>");
		body.Should().Contain("Work sessions to destroy");

		// Every block sizes to the content column, so the page keeps one left edge down the scroll
		// rather than stepping in and out between narrow and full-width panels.
		body.Should().Contain("jt-notice jt-notice--wide");
		body.Should().Contain("jt-record--full");
		body.Should().NotContain("mx-auto");

		// Counted nouns are pluralised, never "3 job(s)", on the screen confirming an irreversible act.
		body.Should().Contain("Delete 3 jobs permanently");
		body.Should().NotContain("job(s)");
	}

	[Fact]
	public async Task An_administrator_deletes_the_whole_subtree()
	{
		var adminId = await SeedEmployeeAsync("subtree.admin-delete", EmployeeRole.Administrator);
		var branch = await AddChildAsync(rootId, adminId, "Branch to wipe");
		var child = await AddChildAsync(branch.Id, adminId, "Child to wipe");
		_ = await AddChildAsync(child.Id, adminId, "Grandchild to wipe");
		var authCookie = await SignInAsync("subtree.admin-delete");

		var (antiforgeryCookie, token) = await GetFormAsync(authCookie, branch.Id);
		var response = await PostAsync(
			authCookie, antiforgeryCookie, token, "Delete", branch.Id, branch.Version, "Cancelled project.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterDelete = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var body = await afterDelete.Content.ReadAsStringAsync();
		body.Should().NotContain("Branch to wipe").And.NotContain("Child to wipe").And.NotContain("Grandchild to wipe");
	}

	[Fact]
	public async Task Deleting_a_subtree_without_a_reason_is_prompted_for_one()
	{
		var adminId = await SeedEmployeeAsync("subtree.admin-no-reason", EmployeeRole.Administrator);
		var branch = await AddChildAsync(rootId, adminId, "Branch needing a reason");
		_ = await AddChildAsync(branch.Id, adminId, "Child needing a reason");
		var authCookie = await SignInAsync("subtree.admin-no-reason");

		var (antiforgeryCookie, token) = await GetFormAsync(authCookie, branch.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, "Delete", branch.Id, branch.Version, null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await response.Content.ReadAsStringAsync()).Should().Contain("reason is required");

		// Nothing was destroyed by the refused attempt.
		var stillThere = await GetAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await stillThere.Content.ReadAsStringAsync()).Should().Contain("Branch needing a reason");
	}

	[Fact]
	public async Task Archiving_the_subtree_instead_needs_no_reason_and_destroys_nothing()
	{
		var adminId = await SeedEmployeeAsync("subtree.admin-archive", EmployeeRole.Administrator);
		var branch = await AddChildAsync(rootId, adminId, "Branch to archive");
		_ = await AddChildAsync(branch.Id, adminId, "Child to archive");
		var authCookie = await SignInAsync("subtree.admin-archive");

		var (antiforgeryCookie, token) = await GetFormAsync(authCookie, branch.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, "Archive", branch.Id, branch.Version, null);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var context = new CommandContext { Actor = administratorId, CorrelationId = Guid.NewGuid() };
		var archivedBranch = await seedClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = branch.Id });
		archivedBranch.Node.ArchivedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task A_stale_version_on_delete_is_reported_as_a_conflict_and_logged()
	{
		var adminId = await SeedEmployeeAsync("subtree.conflict-admin", EmployeeRole.Administrator);
		var branch = await AddChildAsync(rootId, adminId, "Contested branch");
		_ = await AddChildAsync(branch.Id, adminId, "Contested child");
		var authCookie = await SignInAsync("subtree.conflict-admin");

		var (antiforgeryCookie, token) = await GetFormAsync(authCookie, branch.Id);

		// A concurrent edit lands after the form was loaded, advancing the row's version.
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = branch.Id,
			Description = "Concurrently edited",
			OwnerUserId = adminId,
			Priority = Priority.Medium,
			Version = branch.Version,
		});

		var response = await PostAsync(authCookie, antiforgeryCookie, token, "Delete", branch.Id, branch.Version, "Attempted anyway.");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Someone else changed this subtree");
		capturedLogEntries.Should().Contain(entry =>
			entry.Contains("page_concurrency_conflict", StringComparison.Ordinal)
			&& entry.Contains("page=DeleteSubtreeModel", StringComparison.Ordinal));
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
		string authCookie, string antiforgeryCookie, string token, string handler,
		JobNodeId nodeId, long version, string? reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Jobs/DeleteSubtree?handler={handler}");
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

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/DeleteSubtree?nodeId={nodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in DeleteSubtree page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in DeleteSubtree page body.");

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
