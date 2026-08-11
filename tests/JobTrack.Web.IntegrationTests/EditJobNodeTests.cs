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
///     Direct-HTTP tests for the job-node edit workflow (plan §8.5 slice 3), including
///     concurrency-conflict recovery, since <see cref="IJobCommands.EditAsync" /> is a full-replace
///     compare-and-swap on <see cref="EditJobNodeRequest.Version" />.
/// </summary>
public sealed partial class EditJobNodeTests : IAsyncLifetime, IDisposable
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
			UserName = "admin.edit-tests",
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
	public async Task A_job_manager_can_save_edits_to_a_leaf()
	{
		var managerId = await SeedEmployeeAsync("edit.manager", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Pour foundation");
		var authCookie = await SignInAsync("edit.manager");

		var (antiforgeryCookie, token) = await GetEditFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "Pour deeper foundation", managerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterSave = await GetAsync($"/Jobs/Browse?nodeId={leaf.Id.Value}", authCookie);
		(await afterSave.Content.ReadAsStringAsync()).Should().Contain("Pour deeper foundation");
	}

	[Fact]
	public async Task The_edit_page_offers_save_and_cancel_actions()
	{
		var managerId = await SeedEmployeeAsync("edit.actions", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Editable leaf");
		var authCookie = await SignInAsync("edit.actions");

		var response = await GetAsync($"/Jobs/Edit?nodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(">Save<");
		body.Should().Contain(">Cancel<");
		body.Should().NotContain("Preview");
		body.Should().NotContain("Confirm changes");
	}

	[Fact]
	public async Task A_worker_who_does_not_own_the_node_is_denied_on_save()
	{
		var managerId = await SeedEmployeeAsync("edit.owner-manager", EmployeeRole.JobManager);
		var workerId = await SeedEmployeeAsync("edit.denied-worker", EmployeeRole.Worker);
		var leaf = await AddChildAsync(rootId, managerId, "Owned by manager");
		var authCookie = await SignInAsync("edit.denied-worker");

		var (antiforgeryCookie, token) = await GetEditFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "Hijacked description", workerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	/// <summary>
	///     TC-WEB-AUTHZ-003 (threat model row 8): owning one subtree does not grant access to a
	///     sibling subtree. Ownership is reloaded per node inside the command, not treated as a
	///     blanket "this worker owns something" claim.
	/// </summary>
	[Fact]
	public async Task A_worker_who_owns_subtree_a_is_denied_on_save_for_a_node_in_sibling_subtree_b()
	{
		var workerId = await SeedEmployeeAsync("edit.subtree-worker", EmployeeRole.Worker);
		var otherOwnerId = await SeedEmployeeAsync("edit.subtree-other-owner", EmployeeRole.Worker);
		await AddChildAsync(rootId, workerId, "Worker's own subtree A leaf");
		var siblingLeaf = await AddChildAsync(rootId, otherOwnerId, "Sibling subtree B leaf");
		var authCookie = await SignInAsync("edit.subtree-worker");

		var (antiforgeryCookie, token) = await GetEditFormAsync(authCookie, siblingLeaf.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, siblingLeaf.Id, siblingLeaf.Version, "Hijacked sibling edit",
			workerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_stale_version_on_save_is_reported_as_a_conflict_and_the_edit_is_not_lost()
	{
		var managerId = await SeedEmployeeAsync("edit.conflict-manager", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Original description");
		var authCookie = await SignInAsync("edit.conflict-manager");

		var (antiforgeryCookie, token) = await GetEditFormAsync(authCookie, leaf.Id);

		// A concurrent edit lands after the form was loaded, advancing the row's version.
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			NodeId = leaf.Id,
			Description = "Concurrently changed elsewhere",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
			Version = leaf.Version,
		});

		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "My edit", managerId);
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("Someone else changed this node");
		saveBody.Should().Contain("My edit");

		var browseResponse = await GetAsync($"/Jobs/Browse?nodeId={leaf.Id.Value}", authCookie);
		(await browseResponse.Content.ReadAsStringAsync()).Should().Contain("Concurrently changed elsewhere");
	}

	/// <summary>
	///     §2.4: the edit form's <c>NeededStart</c> field round-trips a stored instant back to a
	///     <c>datetime-local</c> string in the viewing employee's own zone, never the server process's.
	/// </summary>
	[Fact]
	public async Task The_edit_form_prefills_NeededStart_formatted_in_the_viewing_employees_own_zone()
	{
		var newYork = DateTimeZoneProviders.Tzdb["America/New_York"];
		var managerId = await SeedEmployeeAsync("edit.zone-prefill", EmployeeRole.JobManager, "America/New_York");
		var stored = CivilTimeResolver.ToInstant(new(2026, 6, 15, 9, 0, 0), newYork);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Node with a needed-start",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
			NeededStart = stored,
		});
		var authCookie = await SignInAsync("edit.zone-prefill");

		var response = await GetAsync($"/Jobs/Edit?nodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("value=\"2026-06-15T09:00\"");
	}

	/// <summary>§2.4: a malformed <c>NeededStart</c> is rejected, never silently reinterpreted or dropped.</summary>
	[Fact]
	public async Task A_malformed_NeededStart_on_save_is_rejected_and_the_node_is_not_changed()
	{
		var managerId = await SeedEmployeeAsync("edit.malformed-needed", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Unchanged description");
		var authCookie = await SignInAsync("edit.malformed-needed");

		var (antiforgeryCookie, token) = await GetEditFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "Attempted change", managerId, "not-a-local-date-time");

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		(await saveResponse.Content.ReadAsStringAsync()).Should().Contain("Enter a valid date and time.");

		var current = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = leaf.Id }, CancellationToken.None);
		current.Node.Description.Should().Be("Unchanged description");
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
		JobNodeId nodeId, long version, string description, AppUserId ownerId, string? neededStart = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Edit");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["NodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["OriginalVersion"] = version.ToString(CultureInfo.InvariantCulture),
			["Input.Description"] = description,
			["Input.OwnerUserId"] = ownerId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.Priority"] = nameof(Priority.Medium),
			["__RequestVerificationToken"] = token,
		};
		if (neededStart is not null) {
			fields["Input.NeededStart"] = neededStart;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetEditFormAsync(string authCookie, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Edit?nodeId={nodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Edit page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Edit page body.");

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

	private async Task<AppUserId> SeedEmployeeAsync(string userName, EmployeeRole role, string ianaTimeZone = "UTC")
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, $ianaTimeZone); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		_ = insertAppUser.Parameters.AddWithValue("$ianaTimeZone", ianaTimeZone);
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
