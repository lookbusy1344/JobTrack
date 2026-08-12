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
			UserName = "admin.move-tests",
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
	public async Task A_job_manager_can_save_moving_a_branch_to_a_new_parent()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "move.manager", EmployeeRole.JobManager);
		var sourceBranch = await AddChildAsync(rootId, managerId, "Source branch");
		var moved = await AddChildAsync(sourceBranch.Id, managerId, "Pour foundation");
		var destinationBranch = await AddChildAsync(rootId, managerId, "Destination branch");
		var authCookie = await client.SignInAsync("move.manager");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, moved.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, moved.Id, moved.Version, destinationBranch.Id);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterSave = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={destinationBranch.Id.Value}", authCookie);
		(await afterSave.Content.ReadAsStringAsync()).Should().Contain("Pour foundation");

		var oldParent = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={sourceBranch.Id.Value}", authCookie);
		(await oldParent.Content.ReadAsStringAsync()).Should().NotContain("Pour foundation");
	}

	[Fact]
	public async Task The_move_page_offers_save_and_cancel_actions()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "move.actions", EmployeeRole.JobManager);
		var moved = await AddChildAsync(rootId, managerId, "Movable branch");
		var authCookie = await client.SignInAsync("move.actions");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Move?nodeId={moved.Id.Value}", authCookie);
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
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "move.owner-manager", EmployeeRole.JobManager);
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "move.denied-worker");
		var moved = await AddChildAsync(rootId, managerId, "Owned by manager");
		var destination = await AddChildAsync(rootId, managerId, "Destination");
		var authCookie = await client.SignInAsync("move.denied-worker");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, moved.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, moved.Id, moved.Version, destination.Id);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task Moving_a_branch_under_its_own_descendant_is_reported_as_a_cycle()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "move.cycle-manager", EmployeeRole.JobManager);
		var branch = await AddChildAsync(rootId, managerId, "Ancestor branch");
		var descendant = await AddChildAsync(branch.Id, managerId, "Descendant branch");
		var authCookie = await client.SignInAsync("move.cycle-manager");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, branch.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, branch.Id, branch.Version, descendant.Id);
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("cycle");
	}

	/// <summary>
	///     A move rejected because it would leave a prerequisite edge connecting an ancestor and a
	///     descendant is not a hierarchy cycle: telling the mover to look at descendants sends them
	///     nowhere near the edge they actually have to remove.
	/// </summary>
	[Fact]
	public async Task Moving_a_node_under_a_job_it_is_a_prerequisite_of_is_reported_as_a_prerequisite_problem()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "move.prerequisite-manager", EmployeeRole.JobManager);
		var required = await AddChildAsync(rootId, managerId, "Site survey");
		var dependent = await AddChildAsync(rootId, managerId, "Excavate foundations");
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
		var authCookie = await client.SignInAsync("move.prerequisite-manager");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, required.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, required.Id, required.Version, dependent.Id);
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("prerequisite");
		saveBody.Should().NotContain("would create a cycle");
	}

	[Fact]
	public async Task A_stale_version_on_save_is_reported_as_a_conflict()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "move.conflict-manager", EmployeeRole.JobManager);
		var moved = await AddChildAsync(rootId, managerId, "Contested leaf");
		var destination = await AddChildAsync(rootId, managerId, "Destination");
		var authCookie = await client.SignInAsync("move.conflict-manager");

		var (antiforgeryCookie, token) = await GetMoveFormAsync(authCookie, moved.Id);

		// A concurrent edit lands after the form was loaded, advancing the row's version.
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
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
		capturedLogEntries.Should().Contain(entry =>
			entry.Contains("page_concurrency_conflict", StringComparison.Ordinal)
			&& entry.Contains("page=MoveModel", StringComparison.Ordinal));
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
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Move page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Move page body.");

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
