namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     §2.1 of the 2026-09-18 fresh-eyes remediation, web side: when two eligible actors start the same
///     fresh leaf at once, the loser now surfaces a <see cref="ConcurrencyConflictException" /> from
///     <see cref="IWorkCommands.StartWorkAsync" /> (the PostgreSQL race test proves the port). The Work
///     page's Start handler must catch it, redirect (PRG) with a retry message, and never return a 500.
///     The conflict cannot be produced against SQLite's serialized writer in-process, so a decorating
///     <see cref="IJobTrackClient" /> throws it on <c>StartWorkAsync</c> while delegating everything
///     else -- so the page still renders and only the Start post conflicts.
/// </summary>
public sealed partial class WorkStartConcurrencyTests : IAsyncLifetime, IDisposable
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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.work-start-concurrency",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;
		administratorId = bootstrapResult.AdministratorId;

		factory = new(database.ConnectionString, jobTrackClient: new FailingStartClient(JobTrackSqlite.Create(database.ConnectionString)));
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
	public async Task A_concurrent_first_start_loser_is_redirected_with_a_retry_message_not_a_500()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.start.loser");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Pour foundation");
		var authCookie = await client.SignInAsync("work.start.loser");

		var (cookie, token) = await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);

		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		reloaded.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("try starting again");
	}

	private async Task<JobNodeResult> AddWorkedLeafAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
		});

		return leaf;
	}

	private async Task<HttpResponseMessage> PostStartAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Start");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["leafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException($"No antiforgery token in {path} body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	/// <summary>
	///     Delegates every capability to <paramref name="inner" /> but replaces <see cref="Work" /> with a
	///     <see cref="FailingStartWorkCommands" /> that throws <see cref="ConcurrencyConflictException" />
	///     from <see cref="IWorkCommands.StartWorkAsync" /> -- the loser of a concurrent first start.
	/// </summary>
	private sealed class FailingStartClient(IJobTrackClient inner) : IJobTrackClient
	{
		public IInstallationCommands Installation => inner.Installation;

		public IJobQueries Query => inner.Query;

		public IEmployeeCommands Employees => inner.Employees;

		public IJobCommands Jobs => inner.Jobs;

		public IWorkCommands Work { get; } = new FailingStartWorkCommands(inner.Work);

		public IScheduleCommands Schedules => inner.Schedules;

		public IRateCommands Rates => inner.Rates;

		public ICostQueries Costs => inner.Costs;

		public IAuditQueries Audit => inner.Audit;

		public ITokenCommands Tokens => inner.Tokens;

		public IRequestCommands Requests => inner.Requests;

		public IAuthenticationAuditCommands AuthenticationAudit => inner.AuthenticationAudit;

		public IAccountCredentialCommands Credentials => inner.Credentials;
	}

	private sealed class FailingStartWorkCommands(IWorkCommands inner) : IWorkCommands
	{
		public Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default) =>
			throw new ConcurrencyConflictException("Simulated concurrent first-start conflict.");

		public Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default) =>
			inner.StartSessionAsync(request, cancellationToken);

		public Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default) =>
			inner.FinishSessionAsync(request, cancellationToken);

		public Task<FinishSessionAndUpdateWriteUpResult> FinishSessionAndUpdateWriteUpAsync(
			FinishSessionAndUpdateWriteUpRequest request, CancellationToken cancellationToken = default) =>
			inner.FinishSessionAndUpdateWriteUpAsync(request, cancellationToken);

		public Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default) =>
			inner.CorrectSessionAsync(request, cancellationToken);

		public Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default) =>
			inner.SetAchievementAsync(request, cancellationToken);

		public Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default) =>
			inner.CompleteLeafAsync(request, cancellationToken);

		public Task<PauseLeafResult> PauseLeafAsync(PauseLeafRequest request, CancellationToken cancellationToken = default) =>
			inner.PauseLeafAsync(request, cancellationToken);

		public Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(
			ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default) =>
			inner.ReopenAndStartWorkAsync(request, cancellationToken);
	}
}
