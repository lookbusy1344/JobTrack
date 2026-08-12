namespace JobTrack.Web.IntegrationTests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
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

	private readonly ConcurrentBag<string> capturedLogEntries = [];

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId administratorId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId rootId;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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

		factory = new(database.ConnectionString, capturedLogEntries);
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
	public async Task A_job_manager_can_delete_an_unused_leaf()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.manager", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Unused leaf");
		var authCookie = await client.SignInAsync("delete.manager");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterDelete = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await afterDelete.Content.ReadAsStringAsync()).Should().NotContain("Unused leaf");
	}

	[Fact]
	public async Task A_leaf_with_unused_leaf_work_deletes_along_with_it()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.unused-leafwork-manager", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Attached but never worked");
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
		});
		var authCookie = await client.SignInAsync("delete.unused-leafwork-manager");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");
	}

	[Fact]
	public async Task A_node_with_children_is_never_offered_the_delete_form()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.parent-manager", EmployeeRole.JobManager);
		var parent = await AddChildAsync(rootId, managerId, "Parent with a child");
		_ = await AddChildAsync(parent.Id, managerId, "Child");
		var authCookie = await client.SignInAsync("delete.parent-manager");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Delete?nodeId={parent.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("cannot be deleted");
		body.Should().NotContain("Delete permanently");
	}

	[Fact]
	public async Task A_non_administrator_is_denied_deleting_a_worked_leaf_with_a_friendly_message()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.denied-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(managerId, "Worked leaf, denied");
		var authCookie = await client.SignInAsync("delete.denied-manager");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "Trying anyway.");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("requires the Administrator role");
	}

	[Fact]
	public async Task An_administrator_deleting_a_worked_leaf_without_a_reason_is_prompted_for_one()
	{
		var adminId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.admin-no-reason", EmployeeRole.Administrator);
		var leaf = await AddWorkedLeafAsync(adminId, "Worked leaf, no reason yet");
		var authCookie = await client.SignInAsync("delete.admin-no-reason");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("requires a reason");
	}

	[Fact]
	public async Task An_administrator_can_delete_a_worked_leaf_with_a_reason()
	{
		var adminId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.admin-with-reason", EmployeeRole.Administrator);
		var leaf = await AddWorkedLeafAsync(adminId, "Worked leaf, deleted with reason");
		var authCookie = await client.SignInAsync("delete.admin-with-reason");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);
		var response = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, "Duplicate of another job; created and worked in error.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");
	}

	/// <summary>
	///     A refused delete is the one failure the reader cannot diagnose from the page: the message
	///     names the invariant, but nothing reaches the log stream, so an operator looking at a
	///     production instance afterwards sees the request succeed with a 200 and no trace of why the
	///     node is still there. The constraint id and the node it was refused for are logged, with the
	///     exception itself carrying the provider's own error (SQLSTATE, constraint name) for the
	///     catch-all cases where the id alone does not identify the offending table.
	/// </summary>
	[Fact]
	public async Task A_refused_delete_is_logged_with_its_constraint_id_and_node()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.logged-refusal", EmployeeRole.JobManager);
		var required = await AddChildAsync(rootId, managerId, "Required job");
		var dependent = await AddChildAsync(rootId, managerId, "Dependent job");
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
		var authCookie = await client.SignInAsync("delete.logged-refusal");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, required.Id);
		var response = await PostAsync(authCookie, antiforgeryCookie, token, required.Id, required.Version, null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		capturedLogEntries.Should().ContainSingle(entry =>
			entry.Contains("job_node_delete_refused", StringComparison.Ordinal)
			&& entry.Contains("constraint=job-node-has-prerequisites-cannot-delete", StringComparison.Ordinal)
			&& entry.Contains(
				$"node_id={required.Id.Value.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_stale_version_on_delete_is_reported_as_a_conflict_and_logged()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "delete.conflict-manager", EmployeeRole.JobManager);
		var leaf = await AddChildAsync(rootId, managerId, "Contested leaf");
		var authCookie = await client.SignInAsync("delete.conflict-manager");

		var (antiforgeryCookie, token) = await GetDeleteFormAsync(authCookie, leaf.Id);

		// A concurrent edit lands after the form was loaded, advancing the row's version.
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			NodeId = leaf.Id,
			Description = "Concurrently edited",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
			Version = leaf.Version,
		});

		var response = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Someone else changed this node");
		capturedLogEntries.Should().Contain(entry =>
			entry.Contains("page_concurrency_conflict", StringComparison.Ordinal)
			&& entry.Contains("page=DeleteModel", StringComparison.Ordinal));
	}

	private async Task<JobNodeResult> AddWorkedLeafAsync(AppUserId ownerId, string description)
	{
		var leaf = await AddChildAsync(rootId, ownerId, description);
		var context = new CommandContext {
			Actor = administratorId,
			CorrelationId = Guid.NewGuid(),
		};
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = context,
			JobNodeId = leaf.Id,
		});
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
		var refreshed = await seedClient.Query.GetJobNodeAsync(new() {
			Context = context,
			NodeId = leaf.Id,
		});
		return leaf with {
			Version = refreshed.Node.Version,
		};
	}

	private async Task<JobNodeResult> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description) =>
		await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
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
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Delete page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Delete page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}







	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();





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

		public void Dispose() { }

		private sealed class CapturingLogger(ConcurrentBag<string> capturedLogEntries) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(
				LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
				capturedLogEntries.Add(formatter(state, exception));
		}
	}
}
