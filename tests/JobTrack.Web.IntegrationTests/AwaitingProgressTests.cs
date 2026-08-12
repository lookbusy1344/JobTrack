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
using NodaTime.Text;
using Pages.Jobs;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the flat "jobs awaiting progress" dashboard: leaves only, filtered by
///     owner and/or subtree, in priority/deadline order. No per-role authorization policy, matching
///     <c>JobTreeBrowsingTests</c> — viewing job data is an unqualified baseline capability for every
///     role (spec §7.3).
/// </summary>
public sealed partial class AwaitingProgressTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	/// <summary>
	///     <c>datetime-local</c> inputs post minute precision, so a backdate assertion has to compare
	///     against a minute-aligned instant rather than "now minus N hours" with its stray seconds.
	/// </summary>
	private const string DateTimeLocalFormat = "yyyy-MM-ddTHH:mm";

	private const int MinutesPerHour = 60;
	private const int HoursBackdated = 2;
	private const int HoursBeforeFinish = 3;
	private const int HoursBeforeNowFinished = 1;

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId? bootstrappedAdminId;
	private JobNodeId? bootstrappedRootId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		seedClient = JobTrackSqlite.Create(database.ConnectionString);

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
	public async Task A_waiting_leaf_appears_and_a_succeeded_leaf_does_not()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.basic");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var doneLeaf = await AddLeafWithWorkAsync(rootId, workerId, "Painting", adminId);
		await SetAchievementAsync(doneLeaf.JobNodeId, Achievement.InProgress, adminId, doneLeaf.Version);
		var inProgress = await seedClient.Query.GetLeafWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = doneLeaf.JobNodeId,
		});
		await SetAchievementAsync(doneLeaf.JobNodeId, Achievement.Success, adminId, inProgress.Version);
		var authCookie = await client.SignInAsync("awaiting.basic");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Painting");
	}

	[Fact]
	/// <summary>
	/// Every dashboard row names its node the same way as the rest of the app -- "Description (ID N)",
	/// via the shared JobNodeDisplay helper -- so a row can be matched back to a report, URL, or
	/// support ticket that only carries the id.
	/// </summary>
	public async Task A_dashboard_row_names_its_node_with_its_id()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.row-id");
		var rootId = bootstrappedRootId!.Value;
		var leaf = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var authCookie = await client.SignInAsync("awaiting.row-id");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain($"Install cabinets (ID {leaf.JobNodeId.Value.ToString(CultureInfo.InvariantCulture)})");
	}

	[Fact]
	public async Task A_leaf_with_no_leaf_work_attached_appears_on_the_dashboard()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.noleafwork");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddChildAsync(rootId, workerId, "Fresh leaf awaiting assignment", adminId);
		var authCookie = await client.SignInAsync("awaiting.noleafwork");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fresh leaf awaiting assignment");
		// No leaf work means no achievement recorded, which draws nothing beside the name -- not a
		// word standing in for the absence, which would mark almost every fresh row on the list.
		body.Should().NotContain("jt-achievement-icon");
	}

	[Fact]
	public async Task A_dashboard_wider_than_the_page_size_offers_a_next_page_link_that_advances_the_offset()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.paging");
		var rootId = bootstrappedRootId!.Value;
		for (var index = 0; index < AwaitingProgressModel.PageSize + 1; ++index) {
			_ = await AddChildAsync(rootId, workerId, $"Leaf {index}", adminId);
		}

		var authCookie = await client.SignInAsync("awaiting.paging");

		var firstResponse = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var firstBody = await firstResponse.Content.ReadAsStringAsync();

		firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		firstBody.Should().Contain("Next page");
		firstBody.Should().Contain($"Offset={AwaitingProgressModel.PageSize}");

		var secondResponse = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?offset={AwaitingProgressModel.PageSize}", authCookie);
		var secondBody = await secondResponse.Content.ReadAsStringAsync();

		secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		secondBody.Should().NotContain("Next page");
	}

	[Fact]
	public async Task A_leaf_blocked_by_an_unsatisfied_prerequisite_still_appears_marked_blocked()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.blocked");
		var rootId = bootstrappedRootId!.Value;
		var required = await AddLeafWithWorkAsync(rootId, workerId, "Required first", adminId);
		var dependent = await AddLeafWithWorkAsync(rootId, workerId, "Blocked leaf", adminId);
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = required.JobNodeId,
			DependentJobId = dependent.JobNodeId,
		});
		var authCookie = await client.SignInAsync("awaiting.blocked");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Blocked leaf");
		body.Should().Contain("Blocked");
	}

	[Fact]
	public async Task Starting_work_from_a_dashboard_row_advances_the_leaf_to_in_progress()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.startwork");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Fresh leaf via dashboard", adminId);
		var authCookie = await client.SignInAsync("awaiting.startwork");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");
		body.Should().Contain("Fresh leaf via dashboard");
		body.Should().Contain("In Progress");
	}

	[Fact]
	public async Task Filtering_by_owner_hides_another_employees_leaf()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.owner");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Worker job", adminId);
		_ = await AddLeafWithWorkAsync(rootId, adminId, "Admin job", adminId);
		var authCookie = await client.SignInAsync("awaiting.owner");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?ownerUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker job");
		body.Should().NotContain("Admin job");
	}

	[Fact]
	public async Task Filtering_by_search_text_hides_a_non_matching_leaf()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.search");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Fit oak cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Paint the fence", adminId);
		var authCookie = await client.SignInAsync("awaiting.search");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress?searchText=oak", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Fit oak cabinets");
		body.Should().NotContain("Paint the fence");
	}

	[Fact]
	public async Task AwaitingProgress_remembers_the_owner_filter_across_a_return_visit()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.filtermem");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Worker job", adminId);
		_ = await AddLeafWithWorkAsync(rootId, adminId, "Admin job", adminId);
		var authCookie = await client.SignInAsync("awaiting.filtermem");

		// Explicitly filter to the worker; capture the session that now remembers the choice.
		using var chooseRequest = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/AwaitingProgress?ownerUserId={workerId.Value}");
		chooseRequest.Headers.Add("Cookie", authCookie);
		var chooseResponse = await client.SendAsync(chooseRequest);
		var sessionCookie = WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(chooseResponse, "JobTrack.Filters") ?? throw new InvalidOperationException("No session cookie was set."));

		// Return with no owner param: the remembered worker filter still applies.
		using var returnRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		returnRequest.Headers.Add("Cookie", $"{authCookie}; {sessionCookie}");
		var returnResponse = await client.SendAsync(returnRequest);
		var body = await returnResponse.Content.ReadAsStringAsync();

		returnResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker job");
		body.Should().NotContain("Admin job");
	}

	[Fact]
	public async Task AwaitingProgress_defaults_to_all_owners_when_nothing_is_remembered()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.default-all");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Worker job", adminId);
		_ = await AddLeafWithWorkAsync(rootId, adminId, "Admin job", adminId);
		var authCookie = await client.SignInAsync("awaiting.default-all");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker job");
		body.Should().Contain("Admin job", "with nothing remembered the dashboard defaults to every owner");
	}

	[Fact]
	public async Task A_blocked_leaf_sorts_below_every_ready_leaf_whatever_its_priority()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.blockedorder");
		var rootId = bootstrappedRootId!.Value;
		var required = await AddLeafWithWorkAsync(rootId, workerId, "Required first", adminId);
		var dependent = await AddLeafAtPriorityAsync(rootId, workerId, "Urgent but blocked", adminId, Priority.Urgent);
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = required.JobNodeId,
			DependentJobId = dependent,
		});
		_ = await AddLeafAtPriorityAsync(rootId, workerId, "Low but ready", adminId, Priority.Low);
		var authCookie = await client.SignInAsync("awaiting.blockedorder");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.IndexOf("Low but ready", StringComparison.Ordinal)
			.Should().BeLessThan(body.IndexOf("Urgent but blocked", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Excluding_blocked_jobs_hides_a_leaf_with_an_unsatisfied_prerequisite()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.excludeblocked");
		var rootId = bootstrappedRootId!.Value;
		var required = await AddLeafWithWorkAsync(rootId, workerId, "Required first", adminId);
		var dependent = await AddLeafWithWorkAsync(rootId, workerId, "Blocked leaf", adminId);
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = required.JobNodeId,
			DependentJobId = dependent.JobNodeId,
		});
		var authCookie = await client.SignInAsync("awaiting.excludeblocked");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress?excludeBlocked=true", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Required first");
		body.Should().NotContain("Blocked leaf");
	}

	[Fact]
	public async Task AwaitingProgress_remembers_the_exclude_blocked_filter_across_a_return_visit()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.blockedmem");
		var rootId = bootstrappedRootId!.Value;
		var required = await AddLeafWithWorkAsync(rootId, workerId, "Required first", adminId);
		var dependent = await AddLeafWithWorkAsync(rootId, workerId, "Blocked leaf", adminId);
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = required.JobNodeId,
			DependentJobId = dependent.JobNodeId,
		});
		var authCookie = await client.SignInAsync("awaiting.blockedmem");

		var sessionCookie = await ChooseFiltersAsync(authCookie, "/Jobs/AwaitingProgress?excludeBlocked=true");
		var body = await ReturnWithRememberedFiltersAsync(authCookie, sessionCookie);

		body.Should().Contain("Required first");
		body.Should().NotContain("Blocked leaf");
	}

	/// <summary>
	///     "In progress" is the achievement, not who is clocked on: a started leaf nobody is currently
	///     working (paused) stays on the list, while one still waiting to be picked up drops off.
	/// </summary>
	[Fact]
	public async Task Showing_only_in_progress_jobs_keeps_a_paused_leaf_and_hides_a_waiting_one()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.inprogress");
		var rootId = bootstrappedRootId!.Value;
		var paused = await AddLeafWithWorkAsync(rootId, workerId, "Started then paused", adminId);
		await SetAchievementAsync(paused.JobNodeId, Achievement.InProgress, adminId, paused.Version);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Not started yet", adminId);
		var authCookie = await client.SignInAsync("awaiting.inprogress");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress?inProgressOnly=true", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Started then paused");
		body.Should().NotContain("Not started yet");
	}

	/// <summary>
	///     The in-progress filter narrows the owner-filtered set rather than replacing it, so the two
	///     together answer "what is this person part-way through".
	/// </summary>
	[Fact]
	public async Task Showing_only_in_progress_jobs_composes_with_the_owner_filter()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.inprogressowner");
		var rootId = bootstrappedRootId!.Value;
		var workerLeaf = await AddLeafWithWorkAsync(rootId, workerId, "Worker started this", adminId);
		await SetAchievementAsync(workerLeaf.JobNodeId, Achievement.InProgress, adminId, workerLeaf.Version);
		var adminLeaf = await AddLeafWithWorkAsync(rootId, adminId, "Admin started this", adminId);
		await SetAchievementAsync(adminLeaf.JobNodeId, Achievement.InProgress, adminId, adminLeaf.Version);
		var authCookie = await client.SignInAsync("awaiting.inprogressowner");

		var response = await client.GetAuthenticatedAsync(
			$"/Jobs/AwaitingProgress?inProgressOnly=true&ownerUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker started this");
		body.Should().NotContain("Admin started this");
	}

	[Fact]
	public async Task AwaitingProgress_remembers_the_in_progress_only_filter_across_a_return_visit()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.inprogressmem");
		var rootId = bootstrappedRootId!.Value;
		var started = await AddLeafWithWorkAsync(rootId, workerId, "Started then paused", adminId);
		await SetAchievementAsync(started.JobNodeId, Achievement.InProgress, adminId, started.Version);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Not started yet", adminId);
		var authCookie = await client.SignInAsync("awaiting.inprogressmem");

		var sessionCookie = await ChooseFiltersAsync(authCookie, "/Jobs/AwaitingProgress?inProgressOnly=true");
		var body = await ReturnWithRememberedFiltersAsync(authCookie, sessionCookie);

		body.Should().Contain("Started then paused");
		body.Should().NotContain("Not started yet");
	}

	/// <summary>
	///     The "Working now" selector answers a different question from the in-progress checkbox: who
	///     is clocked on right now, not what the achievement says. A leaf the worker started and then
	///     paused is in progress but nobody is working it, so it drops out.
	/// </summary>
	[Fact]
	public async Task Filtering_by_the_active_worker_keeps_only_leaves_with_an_open_session_for_that_person()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.activeworker");
		var rootId = bootstrappedRootId!.Value;
		var beingWorked = await AddLeafWithWorkAsync(rootId, workerId, "Worker is on this now", adminId);
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = beingWorked.JobNodeId,
			WorkedByUserId = workerId,
		});
		var paused = await AddLeafWithWorkAsync(rootId, workerId, "Worker paused this", adminId);
		var now = SystemClock.Instance.GetCurrentInstant();
		await AddFinishedSessionAsync(
			workerId, paused.JobNodeId, now - Duration.FromHours(HoursBeforeFinish), now - Duration.FromHours(HoursBeforeNowFinished));
		var authCookie = await client.SignInAsync("awaiting.activeworker");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?activeWorkerUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker is on this now");
		body.Should().NotContain("Worker paused this");
	}

	/// <summary>
	///     An open session is exactly one person's, so selecting a different employee excludes a leaf
	///     someone else is working — this is the "who is doing what in this subtree" question.
	/// </summary>
	[Fact]
	public async Task Filtering_by_the_active_worker_hides_a_leaf_another_employee_is_working()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.activeworkerother");
		var rootId = bootstrappedRootId!.Value;
		var workerLeaf = await AddLeafWithWorkAsync(rootId, workerId, "Worker is on this", adminId);
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = workerLeaf.JobNodeId,
			WorkedByUserId = workerId,
		});
		var adminLeaf = await AddLeafWithWorkAsync(rootId, workerId, "Admin is on this", adminId);
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = adminLeaf.JobNodeId,
			WorkedByUserId = adminId,
		});
		var authCookie = await client.SignInAsync("awaiting.activeworkerother");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?activeWorkerUserId={workerId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Worker is on this");
		body.Should().NotContain("Admin is on this");
	}

	[Fact]
	public async Task AwaitingProgress_remembers_the_active_worker_filter_across_a_return_visit()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.activeworkermem");
		var rootId = bootstrappedRootId!.Value;
		var beingWorked = await AddLeafWithWorkAsync(rootId, workerId, "Worker is on this now", adminId);
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = beingWorked.JobNodeId,
			WorkedByUserId = workerId,
		});
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Nobody is on this", adminId);
		var authCookie = await client.SignInAsync("awaiting.activeworkermem");

		var sessionCookie = await ChooseFiltersAsync(authCookie, $"/Jobs/AwaitingProgress?activeWorkerUserId={workerId.Value}");
		var body = await ReturnWithRememberedFiltersAsync(authCookie, sessionCookie);

		body.Should().Contain("Worker is on this now");
		body.Should().NotContain("Nobody is on this");
	}

	[Fact]
	public async Task AwaitingProgress_remembers_the_search_text_filter_across_a_return_visit()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.searchmem");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Fit oak cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Paint the fence", adminId);
		var authCookie = await client.SignInAsync("awaiting.searchmem");

		var sessionCookie = await ChooseFiltersAsync(authCookie, "/Jobs/AwaitingProgress?searchText=oak");
		var body = await ReturnWithRememberedFiltersAsync(authCookie, sessionCookie);

		body.Should().Contain("Fit oak cabinets");
		body.Should().NotContain("Paint the fence");
	}

	[Fact]
	public async Task AwaitingProgress_remembers_the_unassigned_only_filter_across_a_return_visit()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.poolmem");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Owned job", adminId);
		var unassigned = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Pool job",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		await AttachLeafWorkAsync(unassigned.Id, adminId);
		var authCookie = await client.SignInAsync("awaiting.poolmem");

		var sessionCookie = await ChooseFiltersAsync(authCookie, "/Jobs/AwaitingProgress?unassignedOnly=true");
		var body = await ReturnWithRememberedFiltersAsync(authCookie, sessionCookie);

		body.Should().Contain("Pool job");
		body.Should().NotContain("Owned job");
	}

	/// <summary>
	///     The subtree scope is the one dashboard filter that is deliberately not remembered (ADR 0052,
	///     revised): a URL naming no node — the header nav link, a hand-typed address — always scopes to
	///     the actor's own home node, whatever scope the previous visit chose.
	/// </summary>
	[Fact]
	public async Task AwaitingProgress_forgets_the_subtree_scope_and_returns_to_the_home_node()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.subtreemem");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		var otherBranchId = await AddChildAsync(rootId, workerId, "Bathroom renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(otherBranchId, workerId, "Outside the home node", adminId);
		_ = await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			NodeId = branchId,
		});
		var authCookie = await client.SignInAsync("awaiting.subtreemem");

		// Paired with a filter that *is* remembered, so the recalled cookie is genuinely replayed on the
		// bare visit and the scope's absence from it is what puts the list back on the home node.
		var sessionCookie = await ChooseFiltersAsync(
			authCookie, $"/Jobs/AwaitingProgress?subtreeRootId={otherBranchId.Value}&excludeBlocked=true");
		var body = await ReturnWithRememberedFiltersAsync(authCookie, sessionCookie);

		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Outside the home node");
	}

	/// <summary>
	///     "Show the whole tree" is not remembered either — a bare return visit snaps back to the
	///     home-node default rather than replaying the previous visit's escape hatch.
	/// </summary>
	[Fact]
	public async Task AwaitingProgress_forgets_the_whole_tree_choice_and_returns_to_the_home_node()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.wholetreemem");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Outside the branch", adminId);
		_ = await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			NodeId = branchId,
		});
		var authCookie = await client.SignInAsync("awaiting.wholetreemem");

		// As above: an accompanying remembered filter makes the replayed cookie real, so this asserts
		// forgetting rather than an empty cookie.
		var sessionCookie = await ChooseFiltersAsync(authCookie, "/Jobs/AwaitingProgress?showWholeTree=true&excludeBlocked=true");
		var body = await ReturnWithRememberedFiltersAsync(authCookie, sessionCookie);

		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Outside the branch");
	}

	/// <summary>
	///     Scoping to the tree's root already lists everything, so the escape hatch out of the scope has
	///     nothing to offer and is not drawn — the node itself stays named as the way into Browse.
	/// </summary>
	[Fact]
	public async Task Scoping_to_the_root_offers_no_whole_tree_link()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.rootscope");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Install cabinets", adminId);
		var authCookie = await client.SignInAsync("awaiting.rootscope");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Scoped to");
		body.Should().NotContain(">whole tree</a>");
	}

	/// <summary>
	///     The scope line offers the actor's own home node whenever the dashboard is looking elsewhere —
	///     including while the whole tree is showing — and not when it is already that subtree.
	/// </summary>
	[Fact]
	public async Task The_home_node_link_appears_only_when_the_scope_is_elsewhere()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.homelink");
		var rootId = bootstrappedRootId!.Value;
		var homeId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		var elsewhereId = await AddChildAsync(rootId, workerId, "Bathroom renovation", adminId);
		_ = await AddLeafWithWorkAsync(homeId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(elsewhereId, workerId, "Tile the floor", adminId);
		_ = await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			NodeId = homeId,
		});
		var authCookie = await client.SignInAsync("awaiting.homelink");
		var homeNodeLink = $"/Jobs/AwaitingProgress?subtreeRootId={homeId.Value}\">home node</a>";

		var elsewhere = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={elsewhereId.Value}", authCookie);
		var wholeTree = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={homeId.Value}&showWholeTree=true", authCookie);
		var atHome = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={homeId.Value}", authCookie);

		(await elsewhere.Content.ReadAsStringAsync()).Should().Contain(homeNodeLink);
		(await wholeTree.Content.ReadAsStringAsync()).Should().Contain(homeNodeLink, "the whole tree is not the home node's subtree");
		(await atHome.Content.ReadAsStringAsync()).Should().NotContain(">home node</a>", "the dashboard is already showing it");
	}

	/// <summary>An actor with no home node has nowhere for the link to lead, so it is not drawn.</summary>
	[Fact]
	public async Task The_home_node_link_is_absent_without_a_home_node()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.nohomelink");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		var authCookie = await client.SignInAsync("awaiting.nohomelink");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(">whole tree</a>");
		body.Should().NotContain(">home node</a>");
	}

	/// <summary>
	///     The toolbar's Browse button opens whatever node the dashboard is scoped to, not the viewer's
	///     default landing node.
	/// </summary>
	[Fact]
	public async Task Browse_button_opens_the_scoping_node()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.browsescope");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		var authCookie = await client.SignInAsync("awaiting.browsescope");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain($"/Jobs/Browse?nodeId={branchId.Value}\">Browse</a>");
	}

	/// <summary>
	///     Showing the whole tree is being scoped to its root, so that is the node the line names and
	///     the Browse button opens — not the subtree the request happened to arrive from, and not the
	///     viewer's default landing node.
	/// </summary>
	[Fact]
	public async Task Browse_button_opens_the_root_while_the_whole_tree_is_shown()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.browsewhole");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Outside the branch", adminId);
		_ = await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			NodeId = branchId,
		});
		var authCookie = await client.SignInAsync("awaiting.browsewhole");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}&showWholeTree=true", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Outside the branch", "the whole tree is showing");
		body.Should().Contain($"/Jobs/Browse?nodeId={rootId.Value}\">Browse</a>");
		body.Should().NotContain($"/Jobs/Browse?nodeId={branchId.Value}\">Browse</a>");
	}

	/// <summary>
	///     "Show whole tree" needs no anchor to carry: it resolves to the root, and the root-scoped page
	///     it lands on names the root itself rather than wherever the click came from.
	/// </summary>
	[Fact]
	public async Task Show_whole_tree_scopes_to_the_root()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.wholetreelink");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		var authCookie = await client.SignInAsync("awaiting.wholetreelink");

		var scoped = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}", authCookie);
		var scopedBody = await scoped.Content.ReadAsStringAsync();
		var whole = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}&showWholeTree=true", authCookie);
		var wholeBody = await whole.Content.ReadAsStringAsync();

		scopedBody.Should().Contain("showWholeTree=true\">whole tree</a>");
		scopedBody.Should().NotContain($"subtreeRootId={branchId.Value}&amp;showWholeTree=true", "the whole tree needs no anchor");
		wholeBody.Should().Contain($"/Jobs/Browse?nodeId={rootId.Value}\">Browse</a>", "the root is the scope now");
		wholeBody.Should().NotContain(">whole tree</a>", "the root already is the whole tree");
	}

	[Fact]
	public async Task Scoping_to_a_subtree_hides_a_leaf_outside_it()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.subtree");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Outside the branch", adminId);
		var authCookie = await client.SignInAsync("awaiting.subtree");

		var response = await client.GetAuthenticatedAsync($"/Jobs/AwaitingProgress?subtreeRootId={branchId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Outside the branch");
	}

	[Fact]
	public async Task AwaitingProgress_defaults_to_the_actors_home_node_when_no_subtree_is_specified()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.homedefault");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Outside the branch", adminId);
		_ = await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			NodeId = branchId,
		});
		var authCookie = await client.SignInAsync("awaiting.homedefault");

		// A bare visit -- e.g. following the header's "Awaiting progress" link -- scopes to the actor's
		// own home node rather than the entire tree, but still offers a way back to everything.
		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().NotContain("Outside the branch");
		body.Should().Contain(">whole tree</a>");
	}

	[Fact]
	public async Task Show_the_whole_tree_overrides_the_home_node_default()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.homeoverride");
		var rootId = bootstrappedRootId!.Value;
		var branchId = await AddChildAsync(rootId, workerId, "Kitchen renovation", adminId);
		_ = await AddLeafWithWorkAsync(branchId, workerId, "Install cabinets", adminId);
		_ = await AddLeafWithWorkAsync(rootId, workerId, "Outside the branch", adminId);
		_ = await seedClient.Employees.SetHomeNodeAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			NodeId = branchId,
		});
		var authCookie = await client.SignInAsync("awaiting.homeoverride");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress?showWholeTree=true", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Install cabinets");
		body.Should().Contain("Outside the branch");
		body.Should().NotContain(">whole tree</a>", "the whole tree is already showing, so the escape hatch back to it has nothing to offer");
		body.Should().Contain(">home node</a>", "the way back to the home node stands in its place");
	}

	[Fact]
	public async Task A_leaf_with_an_active_session_shows_no_start_button_only_a_sessions_link()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.toggle");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Toggle leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.toggle");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, formCookie, token, leafId);

		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var startBody = await reloaded.Content.ReadAsStringAsync();
		// Plan §5.3: the dashboard row never finishes inline -- the viewer's own one-click Start
		// is replaced by the always-present "Sessions" link into /Jobs/Work, not an inline finish form.
		startBody.Should().Contain($"/Jobs/Work?leafNodeId={leafId.Value}");
		startBody.Should().Contain("title=\"Sessions\"");
		startBody.Should().NotContain("title=\"Start session\"");
	}

	[Fact]
	/// <summary>
	/// The active-session pill has its own column, so the dashboard and Browse's subtree read the same
	/// way rather than each putting the pill somewhere different. Priority sits beside Deadline in its
	/// own column too -- the two attention-ordering facts (spec: priority, then deadline) read together.
	/// </summary>
	public async Task The_active_session_pill_and_priority_each_have_their_own_column()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.activecolumn");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Active column leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.activecolumn");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, formCookie, token, leafId);
		var reloaded = await client.FollowRedirectAsync(startResponse, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();

		body.Should().Contain("<th class=\"col-10 col-md-5 col-lg-5 col-xxl-3\" aria-label=\"Description\">Desc</th>");
		body.Should().Contain("<th class=\"jt-col-active col-md-2 col-lg-2 d-none d-md-table-cell\">Active</th>");
		// No achievement column: at one twelfth it was narrower than its own heading at every width, so
		// the state rides after the row's name instead, exactly as it does on Browse's subtree tables.
		body.Should().NotContain("jt-col-achievement");
		body.Should().NotContain(">Ach</th>");
		body.Should().Contain("aria-label=\"Priority\">Pri</th>");
		body.Should().Contain("aria-label=\"Deadline\">Due</th>");
		body.Should().Contain("Active since");
	}

	[Fact]
	/// <summary>
	/// Due used to render the full "d MMM yyyy HH:mm" stamp regardless of how far away the deadline
	/// was -- too wide for a column that only has a twelfth of the row. It now follows the same
	/// InstantDisplay.FormatCompact rule as Browse's own Deadline column and the "Active since" pill:
	/// a bare date (no year, no time) once the deadline falls on a different calendar day.
	/// </summary>
	public async Task Due_shows_a_bare_date_for_a_deadline_on_a_different_day()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.due-compact-date");
		var rootId = bootstrappedRootId!.Value;
		var deadline = Instant.FromUtc(2030, 1, 1, 12, 0);
		_ = await AddChildWithDeadlineAsync(rootId, workerId, "Due compact date leaf", adminId, deadline);
		var authCookie = await client.SignInAsync("awaiting.due-compact-date");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("1 Jan", "a deadline on a different day should show its bare date");
		body.Should().NotContain("1 Jan 2030 12:00", "the full date-and-time stamp is too wide for the Due column");
	}

	[Fact]
	/// <summary>
	/// The other half of the same rule: a deadline due today shows only the time-of-day, not a date --
	/// the calendar date is already implied by "today". Checked as the absence of the full stamp's
	/// year rather than an exact HH:mm match, since the two clock reads (seeding "now", then rendering
	/// the page) are not guaranteed to land in the same minute.
	/// </summary>
	public async Task Due_shows_a_bare_time_for_a_deadline_due_today()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.due-compact-time");
		var rootId = bootstrappedRootId!.Value;
		var dueToday = SystemClock.Instance.GetCurrentInstant();
		_ = await AddChildWithDeadlineAsync(rootId, workerId, "Due compact time leaf", adminId, dueToday);
		var authCookie = await client.SignInAsync("awaiting.due-compact-time");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var bareTime = dueToday.InZone(DateTimeZone.Utc).TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
		body.Should().Contain(bareTime, "a deadline due today should show a bare time");
		var todayYear = dueToday.InZone(DateTimeZone.Utc).Year.ToString(CultureInfo.InvariantCulture);
		body.Should().NotContain($"{todayYear} {bareTime}",
			"a deadline due today should not show the full date-and-time stamp");
	}

	[Fact]
	/// <summary>
	///     Due follows the same overdue rule (InstantDisplay.IsPast, jt-overdue) as Browse's own
	///     Deadline column and record-view deadline.
	/// </summary>
	public async Task Due_renders_red_only_once_the_deadline_has_passed()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.due-overdue");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddChildWithDeadlineAsync(rootId, workerId, "Overdue due leaf", adminId, Instant.FromUtc(2020, 1, 1, 12, 0));
		_ = await AddChildWithDeadlineAsync(rootId, workerId, "Future due leaf", adminId, Instant.FromUtc(2030, 1, 1, 12, 0));
		var authCookie = await client.SignInAsync("awaiting.due-overdue");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("class=\"jt-overdue\">1 Jan</span>", "a deadline that has already passed should render red");
		body.Should().Contain("<span>1 Jan</span>", "a deadline still to come should not render red");
	}

	[Fact]
	/// <summary>
	/// The achievement rides after the row's name, as it does on Browse's subtree tables, and a leaf
	/// with no achievement recorded draws nothing at all rather than a word standing in for absence.
	/// </summary>
	public async Task An_achievement_follows_the_row_name_and_none_draws_nothing()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.achievementinline");
		var rootId = bootstrappedRootId!.Value;
		var started = await AddLeafWithWorkAsync(rootId, workerId, "Started leaf", adminId);
		await SetAchievementAsync(started.JobNodeId, Achievement.InProgress, adminId, started.Version);
		_ = await AddChildAsync(rootId, workerId, "Untouched leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.achievementinline");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var startedIndex = body.IndexOf("Started leaf", StringComparison.Ordinal);
		var iconIndex = body.IndexOf("jt-achievement-icon--in-progress", StringComparison.Ordinal);
		startedIndex.Should().BeGreaterThan(0);
		iconIndex.Should().BeGreaterThan(startedIndex);
		body.Should().Contain("Untouched leaf");
		body.Should().NotContain(">None</span>");
	}

	[Fact]
	public async Task Finishing_work_from_the_dashboard_returns_the_row_to_a_start_button()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.finish");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Finish leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.finish");

		var (startFormCookie, startToken) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, startFormCookie, startToken, leafId);
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		await client.FollowRedirectAsync(startResponse, authCookie);
		var session = (await GetSessionsAsync(leafId, adminId)).Should().ContainSingle().Subject;

		var (workFormCookie, workToken) = await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leafId.Value}");
		var finishResponse = await PostFinishWorkAsync(authCookie, workFormCookie, workToken, leafId, session.Id.Value, session.Version);
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await client.FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();

		finishBody.Should().Contain("Ends this session; the job stays In Progress.");
		finishBody.Should().Contain("#jt-icon-start");

		using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		dashboardRequest.Headers.Add("Cookie", authCookie);
		var dashboardResponse = await client.SendAsync(dashboardRequest);
		var dashboardBody = await dashboardResponse.Content.ReadAsStringAsync();
		dashboardBody.Should().Contain("title=\"Start session\"");
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_time_from_the_dashboard_row()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.backdate-start");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated start leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.backdate-start");
		var backdatedAt = MinutesAgo(HoursBackdated * MinutesPerHour);

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, FormatForDateTimeLocal(backdatedAt));

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Session started");
		body.Should().Contain("title=\"Sessions\"");

		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(Instant.FromDateTimeOffset(backdatedAt));
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_time_from_the_dashboard_row_shows_a_helpful_error()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.future-start");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Future start leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.future-start");
		var future = FormatForDateTimeLocal(MinutesAgo(-HoursBackdated * MinutesPerHour));

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, future);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("in the future");
	}

	[Fact]
	public async Task Starting_a_session_with_a_malformed_backdate_from_the_dashboard_row_does_not_start_work()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.malformed-start");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Malformed dashboard start", adminId);
		var authCookie = await client.SignInAsync("awaiting.malformed-start");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, "not-a-local-date-time");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Enter a valid date and time.");
		(await GetSessionsAsync(leafId, adminId)).Should().BeEmpty();
	}

	[Fact]
	public async Task A_worker_can_finish_a_session_with_a_backdated_time_reached_from_the_dashboard_row()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.backdate-finish");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated finish leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.backdate-finish");
		var startedAt = MinutesAgo(HoursBeforeFinish * MinutesPerHour);
		var finishedAt = MinutesAgo(HoursBeforeNowFinished * MinutesPerHour);

		var (startFormCookie, startToken) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var startResponse = await PostStartWorkAsync(authCookie, startFormCookie, startToken, leafId, FormatForDateTimeLocal(startedAt));
		startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		await client.FollowRedirectAsync(startResponse, authCookie);
		var session = (await GetSessionsAsync(leafId, adminId)).Should().ContainSingle().Subject;

		var (workFormCookie, workToken) = await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leafId.Value}");
		var finishResponse =
			await PostFinishWorkAsync(authCookie, workFormCookie, workToken, leafId, session.Id.Value, session.Version,
				FormatForDateTimeLocal(finishedAt));
		finishResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var finishReloaded = await client.FollowRedirectAsync(finishResponse, authCookie);
		var finishBody = await finishReloaded.Content.ReadAsStringAsync();
		finishBody.Should().Contain("Ends this session; the job stays In Progress.");

		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.FinishedAt.Should().Be(Instant.FromDateTimeOffset(finishedAt));
	}

	[Fact]
	// Auckland is deep in southern winter (NZST, UTC+12, no DST) in June, and never coincides with
	// whatever zone the test process's own machine happens to run in -- so this proves the backdate
	// was resolved in the *employee's own* zone, not the server's.
	public async Task Backdating_and_viewing_a_session_both_use_the_viewing_employees_own_zone_not_the_servers()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.tz-auckland", "Pacific/Auckland");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated in Auckland's own zone", adminId);
		var authCookie = await client.SignInAsync("awaiting.tz-auckland");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, "2026-06-15T09:00");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(Instant.FromUtc(2026, 6, 14, 21, 0),
			"09:00 NZST (UTC+12) on 15 June is 21:00 UTC the day before");

		// Reloading the dashboard as the same Auckland-zoned employee must show the wall clock back
		// converted through the same zone as the write, not UTC. The "Active since" pill only shows a
		// compact date (InstantDisplay.FormatCompact) for a non-today session, dropping the time --
		// but 15 June is still the proof: in UTC this instant falls on the 14th, so only a correct
		// Auckland conversion produces the 15th.
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("15 Jun");
	}

	[Fact]
	// The UK's spring-forward transition is a fixed EU-wide rule (last Sunday in March, 01:00 UTC), so
	// 01:00-01:59 local time on 2026-03-29 never occurs -- this proves a backdate landing in that gap
	// is resolved through the same CivilTimeResolver policy (ADR 0008) as the rest of the app, not a
	// naive parse that would throw or silently pick an arbitrary instant.
	public async Task Backdating_into_a_dst_gap_resolves_via_the_shared_civil_time_resolver()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.tz-dst-gap", "Europe/London");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Backdated into a spring-forward gap", adminId);
		var authCookie = await client.SignInAsync("awaiting.tz-dst-gap");

		const string gapLocalWallClock = "2026-03-29T01:30";
		var londonZone = DateTimeZoneProviders.Tzdb["Europe/London"];
		var expected = CivilTimeResolver.ToInstant(
			LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-dd'T'HH:mm").Parse(gapLocalWallClock).Value, londonZone);

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId, gapLocalWallClock);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var sessions = await GetSessionsAsync(leafId, adminId);
		sessions.Should().ContainSingle().Which.StartedAt.Should().Be(expected);
	}

	[Fact]
	public async Task The_dashboard_row_offers_start_as_an_icon_beside_a_backdate_disclosure()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.icons");
		var rootId = bootstrappedRootId!.Value;
		_ = await AddChildAsync(rootId, workerId, "Icon row leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.icons");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("#jt-icon-start");
		body.Should().Contain("#jt-icon-backdate");
		body.Should().Contain("name=\"startedAt\"");
	}

	[Fact]
	public async Task The_dashboard_row_shows_only_the_sessions_icon_once_a_session_is_active()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.finish-icon");
		var rootId = bootstrappedRootId!.Value;
		var leafId = await AddChildAsync(rootId, workerId, "Finish icon leaf", adminId);
		var authCookie = await client.SignInAsync("awaiting.finish-icon");

		var (formCookie, token) = await GetFormAsync(authCookie, "/Jobs/AwaitingProgress");
		var response = await PostStartWorkAsync(authCookie, formCookie, token, leafId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("#jt-icon-sessions");
		body.Should().NotContain("btn btn-secondary\">Finish / pause");
	}

	[Fact]
	public async Task The_dashboard_shows_current_costs_for_paused_and_in_progress_jobs()
	{
		var (adminId, workerId) = await BootstrapAndSeedWorkerAsync("awaiting.costs");
		_ = await SeedWorkerEmployeeAsync("awaiting.costs.viewer", EmployeeRole.CostViewer);
		var rootId = bootstrappedRootId!.Value;
		var pausedLeaf = await AddChildAsync(rootId, workerId, "Paused costed leaf", adminId);
		var activeLeaf = await AddChildAsync(rootId, workerId, "Active costed leaf", adminId);
		await AttachLeafWorkAsync(pausedLeaf, adminId);
		await AttachLeafWorkAsync(activeLeaf, adminId);

		var now = SystemClock.Instance.GetCurrentInstant();
		await AddWorkingWindowAsync(workerId, adminId, now - Duration.FromDays(1), now - Duration.FromDays(1) + Duration.FromHours(9));
		await AddWorkingWindowAsync(workerId, adminId, now - Duration.FromHours(2), now + Duration.FromHours(1));
		await AddUserCostRateAsync(workerId, adminId, 25m, now - Duration.FromDays(2));
		await AddFinishedSessionAsync(workerId, pausedLeaf, now - Duration.FromDays(1), now - Duration.FromDays(1) + Duration.FromHours(8));
		_ = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = activeLeaf,
			WorkedByUserId = workerId,
			StartedAt = now - Duration.FromHours(1),
		});
		var authCookie = await client.SignInAsync("awaiting.costs.viewer");

		var response = await client.GetAuthenticatedAsync("/Jobs/AwaitingProgress", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Paused costed leaf");
		body.Should().Contain("Active costed leaf");
		body.Should().Contain(">&#xA3;200.00 /&#xA0;8.0 hrs<");

		// The active leaf's session is still running: its accrued cost grows with real elapsed time
		// between the `now` captured above and this request actually rendering, so an exact string match
		// is a wall-clock race. £25/hour accrues a penny every 1.44s of drift, so tolerate a few minutes
		// of slack rather than pinning an exact value.
		var activeLeafCosts = MoneyAmountPattern().Matches(body)
			.Select(match => decimal.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture));
		activeLeafCosts.Should().Contain(amount => amount >= 25.00m && amount <= 25.50m);
	}

	[Fact]
	public async Task An_unauthenticated_request_is_redirected_to_sign_in()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	private async Task<(AppUserId AdministratorId, AppUserId WorkerId)> BootstrapAndSeedWorkerAsync(
		string workerUserName, string workerIanaTimeZone = "UTC")
	{
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = $"admin.{workerUserName}",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});

		bootstrappedRootId = bootstrapResult.RootJobNodeId;
		bootstrappedAdminId = bootstrapResult.AdministratorId;

		var workerId = await SeedWorkerEmployeeAsync(workerUserName, ianaTimeZone: workerIanaTimeZone);

		return (bootstrapResult.AdministratorId, workerId);
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<JobNodeId> AddChildWithDeadlineAsync(
		JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId, Instant neededFinish)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
			NeededFinish = neededFinish,
		});

		return result.Id;
	}

	private async Task<JobNodeId> AddLeafAtPriorityAsync(
		JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId, Priority priority)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = priority,
		});
		await AttachLeafWorkAsync(leaf.Id, adminId);

		return leaf.Id;
	}

	/// <summary>
	///     Applies <paramref name="path" />'s filters explicitly and returns the session cookie that now
	///     remembers them, for <see cref="ReturnWithRememberedFiltersAsync" /> to replay.
	/// </summary>
	private async Task<string> ChooseFiltersAsync(string authCookie, string path)
	{
		using var chooseRequest = new HttpRequestMessage(HttpMethod.Get, path);
		chooseRequest.Headers.Add("Cookie", authCookie);
		var chooseResponse = await client.SendAsync(chooseRequest);

		return WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(chooseResponse, "JobTrack.Filters") ?? throw new InvalidOperationException("No session cookie was set."));
	}

	/// <summary>A bare return visit carrying no filter parameters at all — every one must be recalled.</summary>
	private async Task<string> ReturnWithRememberedFiltersAsync(string authCookie, string sessionCookie)
	{
		using var returnRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		returnRequest.Headers.Add("Cookie", $"{authCookie}; {sessionCookie}");
		var returnResponse = await client.SendAsync(returnRequest);
		returnResponse.StatusCode.Should().Be(HttpStatusCode.OK);

		return await returnResponse.Content.ReadAsStringAsync();
	}

	private async Task<LeafWorkResult> AddLeafWithWorkAsync(JobNodeId parentId, AppUserId ownerId, string description, AppUserId adminId)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

		return await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id });
	}

	private async Task AttachLeafWorkAsync(JobNodeId leafId, AppUserId adminId) =>
		_ = await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() }, JobNodeId = leafId });

	private async Task AddWorkingWindowAsync(AppUserId workerId, AppUserId adminId, Instant start, Instant end) =>
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(start, end),
				null),
			Reason = "Working window for awaiting-progress cost test",
		});

	private async Task AddUserCostRateAsync(AppUserId workerId, AppUserId adminId, decimal amountPerHour, Instant effectiveStart) =>
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Rate = new(new(amountPerHour), effectiveStart, null),
		});

	private async Task AddFinishedSessionAsync(AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = startedAt,
		});

		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = finishedAt,
		});
	}

	private async Task SetAchievementAsync(JobNodeId leafId, Achievement newAchievement, AppUserId adminId, long version) =>
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = adminId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			NewAchievement = newAchievement,
			Reason = "Test transition",
			Version = version,
		});

	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>




	/// <summary>
	///     A minute-aligned UTC wall time, <paramref name="minutes" /> ago. UTC, not the test process's
	///     own local zone, because a <c>datetime-local</c> backdate posts a bare wall time with no
	///     offset and is now resolved in the *viewing employee's own* zone (<c>BackdateInstant</c>,
	///     <c>IViewerTimeZoneResolver</c>) — this suite's worker is seeded with
	///     <c>
	///         iana_time_zone =
	///         'UTC'
	///     </c>
	///     (<see cref="SeedWorkerEmployeeAsync" />), so a UTC-based wall time round-trips
	///     regardless of what zone the test process itself happens to run in.
	/// </summary>
	private static DateTimeOffset MinutesAgo(int minutes)
	{
		var now = DateTimeOffset.UtcNow;

		return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(-minutes);
	}

	private static string FormatForDateTimeLocal(DateTimeOffset value) => value.ToString(DateTimeLocalFormat, CultureInfo.InvariantCulture);

	private async Task<EquatableArray<WorkSessionResult>> GetSessionsAsync(JobNodeId leafId, AppUserId actor) =>
		await seedClient.Query.GetLeafSessionsAsync(
			new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, LeafWorkId = leafId },
			CancellationToken.None);

	private async Task<HttpResponseMessage> PostStartWorkAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId jobNodeId,
		string? startedAt = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/AwaitingProgress?handler=StartWork");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["jobNodeId"] = jobNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (startedAt is not null) {
			fields["startedAt"] = startedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	/// <summary>
	///     Ending a session from the dashboard is a two-step navigation (plan §5.3): the row's "End
	///     session" link opens <c>/Jobs/Work</c>, whose own Finish handler actually posts the finish.
	///     This mirrors that by posting directly to <c>/Jobs/Work?handler=Finish</c>, the same handler
	///     the dashboard's End-session link ultimately drives.
	/// </summary>
	private async Task<HttpResponseMessage> PostFinishWorkAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId,
		long sessionId, long version, string? finishedAt = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Finish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["leafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["sessionId"] = sessionId.ToString(CultureInfo.InvariantCulture),
			["version"] = version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (finishedAt is not null) {
			fields["finishedAt"] = finishedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

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





	[GeneratedRegex(">&#xA3;(?<amount>[0-9]+\\.[0-9]{2})(?= /|<)")]
	private static partial Regex MoneyAmountPattern();

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private async Task<AppUserId> SeedWorkerEmployeeAsync(string userName, EmployeeRole role = EmployeeRole.Worker, string ianaTimeZone = "UTC")
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



}
