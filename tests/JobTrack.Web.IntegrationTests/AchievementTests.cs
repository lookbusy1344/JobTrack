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
///     Direct-HTTP tests for achievement updates (unified-leaf-workflow plan Stage 5, ADR 0001/0045):
///     <c>/Jobs/Achievement</c> is now a compatibility redirect to <c>/Jobs/Work</c>'s status section,
///     and every transition -- including the reopening-authority rule (Administrator/JobManager only,
///     regardless of ownership) -- is exercised through the unified page's <c>SetAchievement</c>
///     handler instead of the retired standalone form.
/// </summary>
public sealed partial class AchievementTests : IAsyncLifetime, IDisposable
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
			UserName = "admin.achievement-tests",
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
	public async Task Getting_the_achievement_page_redirects_to_the_unified_work_pages_status_section()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.redirect", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Redirect check");
		var authCookie = await client.SignInAsync("achievement.redirect");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Achievement?jobNodeId={leaf.Id.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Be($"/Jobs/Work?leafNodeId={leaf.Id.Value}#status");
	}

	[Fact]
	public async Task The_work_page_shows_humanized_achievement_labels_not_raw_enum_names()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.dropdown", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Label check");
		var authCookie = await client.SignInAsync("achievement.dropdown");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Waiting");
		body.Should().NotContain(">InProgress<");
	}

	[Fact]
	public async Task A_worker_can_move_their_own_leaf_forward_to_in_progress()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.worker", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Pour foundation");
		var authCookie = await client.SignInAsync("achievement.worker");

		var (cookie, token) = await GetAntiforgeryAsync(authCookie, leaf.Id);
		var response = await PostSetAchievementAsync(
			authCookie, cookie, token, leaf.Id, nameof(Achievement.InProgress), "Started work.", 1);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Work");
	}

	[Fact]
	public async Task A_worker_cannot_reopen_a_terminal_achievement()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.reopen-worker", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Cancelled job");
		var cancelled = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Cancelled,
			Reason = "Client withdrew the request.",
			Version = 1,
		});

		var authCookie = await client.SignInAsync("achievement.reopen-worker");
		var (cookie, token) = await GetAntiforgeryAsync(authCookie, leaf.Id);
		var response = await PostSetAchievementAsync(
			authCookie, cookie, token, leaf.Id, nameof(Achievement.Waiting), "Reconsidered.", cancelled.Version);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task An_administrator_can_reopen_a_terminal_achievement()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.reopen-target", EmployeeRole.Worker);
		var adminUserId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.reopen-admin", EmployeeRole.Administrator);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Cancelled job for reopening");
		var cancelled = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Cancelled,
			Reason = "Client withdrew the request.",
			Version = 1,
		});

		var authCookie = await client.SignInAsync("achievement.reopen-admin");
		var (cookie, token) = await GetAntiforgeryAsync(authCookie, leaf.Id);
		var response = await PostSetAchievementAsync(
			authCookie, cookie, token, leaf.Id, nameof(Achievement.Waiting), "Reconsidered.", cancelled.Version);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Jobs/Work");
		_ = adminUserId;
	}

	[Fact]
	public async Task The_change_outcome_dropdown_offers_success_for_an_in_progress_leaf_not_only_cancel_or_unsuccessful()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.dropdown-success", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Dropdown offers success");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("achievement.dropdown-success");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("id=\"changeOutcomeAchievement\"");
		body.Should().Contain("value=\"Success\"");
		body.Should().Contain("value=\"Cancelled\"");
		body.Should().Contain("value=\"Unsuccessful\"");
	}

	[Fact]
	public async Task A_controlling_worker_can_change_an_in_progress_leafs_outcome_to_success_via_the_change_outcome_dropdown()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "achievement.dropdown-set-success", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Change outcome to success");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			SessionId = session.Id,
			Version = session.Version,
		});
		var authCookie = await client.SignInAsync("achievement.dropdown-set-success");

		var (cookie, token) = await GetAntiforgeryAsync(authCookie, leaf.Id);
		var response = await PostSetAchievementAsync(
			authCookie, cookie, token, leaf.Id, nameof(Achievement.Success), "Finished ahead of schedule.", 2);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id });
		leafWork.Achievement.Should().Be(Achievement.Success);
	}

	private async Task<JobNodeResult> AddWorkedLeafAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});

		return leaf;
	}

	private async Task<HttpResponseMessage> PostSetAchievementAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId jobNodeId, string newAchievement, string reason, long version)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=SetAchievement");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = jobNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["leafWorkVersion"] = version.ToString(CultureInfo.InvariantCulture),
			["newAchievement"] = newAchievement,
			["reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetAntiforgeryAsync(string authCookie, JobNodeId jobNodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={jobNodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Work page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Work page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}





	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();





}
