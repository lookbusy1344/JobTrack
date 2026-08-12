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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.manager", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var beforeSave = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await beforeSave.Content.ReadAsStringAsync()).Should().NotContain("Pour foundation");

		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, "Pour foundation", managerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterSave = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await afterSave.Content.ReadAsStringAsync()).Should().Contain("Pour foundation");
	}

	[Fact]
	public async Task The_create_page_has_no_branch_or_leaf_selector()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.no-kind", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.no-kind");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Create?parentId={rootId.Value}", authCookie);
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
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.xss-manager", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.xss-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, InjectedDescription, managerId);
		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var browseResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();

		browseBody.Should().NotContain(InjectedDescription);
		browseBody.Should().Contain("&lt;script&gt;alert(&#x27;xss&#x27;)&lt;/script&gt;");
	}

	[Fact]
	public async Task A_worker_who_does_not_own_the_parent_is_denied_on_save()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.denied-worker", EmployeeRole.Worker);
		var authCookie = await client.SignInAsync("create.denied-worker");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, "Unauthorized child", workerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_worker_who_owns_the_parent_can_create_a_child_under_it()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.owning-worker", EmployeeRole.Worker);
		var branchResult = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Worker-owned branch",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		var authCookie = await client.SignInAsync("create.owning-worker");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, branchResult.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, branchResult.Id, "Owned child", workerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");
	}

	[Fact]
	public async Task A_job_manager_can_create_an_unassigned_child_from_a_blank_owner_field()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.unassigned-manager", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.unassigned-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, "Pool child from web", null);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var browseResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}&unassignedOnly=true", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();
		browseBody.Should().Contain("Pool child from web");
	}

	[Fact]
	public async Task Creating_under_a_nonexistent_parent_shows_an_error()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.missing-parent", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.missing-parent");
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
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.worked-parent-manager", EmployeeRole.JobManager);
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
		var authCookie = await client.SignInAsync("create.worked-parent-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, leaf.Id, "Invalid child", managerId);
		var body = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("already has work attached");
	}

	[Fact]
	public async Task The_create_page_offers_a_begin_work_selector_defaulting_to_none()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.begin-work-form", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.begin-work-form");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Create?parentId={rootId.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("Begin work for");
		// Nothing in this select is pre-selected — unlike Owner and Priority, which default to the actor
		// and Medium — so the browser falls back to its first option, which must be "None".
		body.Should().Contain("name=\"Input.BeginWorkForUserId\"><option value=\"\">None</option>");
		BeginWorkSelectPattern().Match(body).Groups["options"].Value.Should().NotContain("selected");
	}

	[Fact]
	public async Task Choosing_a_worker_in_begin_work_for_creates_the_child_already_in_progress_with_an_open_session()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.begin-work-manager", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.begin-work-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, rootId, "Started on creation", managerId, beginWorkForUserId: managerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var created = await FindChildNodeAsync(rootId, "Started on creation");
		var page = await GetLeafWorkPageAsync(created.Id);

		page.HasLeafWork.Should().BeTrue();
		page.Achievement.Should().Be(Achievement.InProgress);
		page.ActiveSessions.Should().ContainSingle().Which.WorkedByUserId.Should().Be(managerId);
	}

	[Fact]
	public async Task Leaving_begin_work_for_at_none_creates_a_child_with_no_leaf_work()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.begin-work-none", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.begin-work-none");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(authCookie, antiforgeryCookie, token, rootId, "Nobody started this", managerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var created = await FindChildNodeAsync(rootId, "Nobody started this");
		var page = await GetLeafWorkPageAsync(created.Id);

		page.HasLeafWork.Should().BeFalse();
		page.ActiveSessions.Should().BeEmpty();
	}

	/// <summary>
	///     The new node inherits its parent's unsatisfied prerequisite, so the whole create is refused —
	///     the page must say why rather than leaving a half-created node behind.
	/// </summary>
	[Fact]
	public async Task Beginning_work_on_a_child_of_a_blocked_parent_shows_an_error_and_creates_nothing()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.begin-work-blocked", EmployeeRole.JobManager);
		var context = new CommandContext { Actor = administratorId, CorrelationId = Guid.NewGuid() };
		var required = await seedClient.Jobs.AddChildAsync(new() {
			Context = context,
			ParentId = rootId,
			Description = "Unfinished prerequisite",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
		});
		var anchor = await seedClient.Jobs.AddChildAsync(new() {
			Context = context,
			ParentId = rootId,
			Description = "Blocked branch",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
		});
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			RequiredJobId = required.Id,
			DependentJobId = anchor.Id,
		});
		var authCookie = await client.SignInAsync("create.begin-work-blocked");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, anchor.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, anchor.Id, "Blocked child", managerId, beginWorkForUserId: managerId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		(await saveResponse.Content.ReadAsStringAsync()).Should().Contain("prerequisites are not satisfied");

		var children = await seedClient.Query.GetJobChildrenAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, ParentId = anchor.Id },
			CancellationToken.None);
		children.Should().BeEmpty();
	}

	[Fact]
	public async Task Beginning_work_for_an_ineligible_employee_shows_an_error_and_creates_nothing()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.begin-work-ineligible-manager", EmployeeRole.JobManager);
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.begin-work-requester", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("create.begin-work-ineligible-manager");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, rootId, "Ineligible worker child", managerId, beginWorkForUserId: requesterId);

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		(await saveResponse.Content.ReadAsStringAsync()).Should().Contain("no longer eligible");
		var afterSave = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await afterSave.Content.ReadAsStringAsync()).Should().NotContain("Ineligible worker child");
	}

	private async Task<LeafWorkPageResult> GetLeafWorkPageAsync(JobNodeId nodeId) =>
		await seedClient.Query.GetLeafWorkPageAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, JobNodeId = nodeId },
			CancellationToken.None);

	/// <summary>
	///     §2.4: a malformed <c>NeededStart</c> must be rejected before the command runs, not silently
	///     reinterpreted or dropped.
	/// </summary>
	[Fact]
	public async Task A_malformed_NeededStart_is_rejected_without_creating_the_child()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.malformed-needed", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("create.malformed-needed");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, rootId, "Malformed needed-start child", managerId, "not-a-local-date-time");

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		(await saveResponse.Content.ReadAsStringAsync()).Should().Contain("Enter a valid date and time.");
		var afterSave = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={rootId.Value}", authCookie);
		(await afterSave.Content.ReadAsStringAsync()).Should().NotContain("Malformed needed-start child");
	}

	/// <summary>
	///     §2.4: <c>NeededStart</c> is a bare wall-clock string resolved in the viewing employee's own
	///     zone (<c>BackdateInstant</c>), never the server process's own OS zone. The employee here is
	///     seeded with <c>America/New_York</c>, deliberately different from whatever zone this test
	///     process itself runs in, so the assertion only holds if resolution actually used the viewer's
	///     zone.
	/// </summary>
	[Fact]
	public async Task NeededStart_is_resolved_in_the_viewing_employees_own_zone_not_the_server_process_zone()
	{
		var newYork = DateTimeZoneProviders.Tzdb["America/New_York"];
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.viewer-zone", EmployeeRole.JobManager, "America/New_York");
		var authCookie = await client.SignInAsync("create.viewer-zone");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, rootId, "Zoned needed-start child", managerId, "2026-06-15T09:00");
		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var created = await FindChildNodeAsync(rootId, "Zoned needed-start child");
		created.NeededStart.Should().Be(CivilTimeResolver.ToInstant(new(2026, 6, 15, 9, 0, 0), newYork));
	}

	/// <summary>
	///     §2.4 DST coverage: 2024-03-10 springs forward in <c>America/New_York</c>, so 02:30 local never
	///     occurs. <c>CivilTimeResolver</c> shifts it forward by the gap length (spec/ADR 0008) rather
	///     than throwing or silently picking a nearby offset, and the create form must follow the exact
	///     same policy as every other backdate path in the app.
	/// </summary>
	[Fact]
	public async Task A_NeededStart_landing_in_a_spring_forward_gap_shifts_forward_by_the_gap_length()
	{
		var newYork = DateTimeZoneProviders.Tzdb["America/New_York"];
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "create.dst-gap", EmployeeRole.JobManager, "America/New_York");
		var authCookie = await client.SignInAsync("create.dst-gap");

		var (antiforgeryCookie, token) = await GetCreateFormAsync(authCookie, rootId);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, rootId, "DST gap needed-start child", managerId, "2024-03-10T02:30");
		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var created = await FindChildNodeAsync(rootId, "DST gap needed-start child");
		created.NeededStart.Should().Be(CivilTimeResolver.ToInstant(new(2024, 3, 10, 2, 30, 0), newYork));
		created.NeededStart!.Value.InZone(newYork).LocalDateTime.Should().Be(new(2024, 3, 10, 3, 30, 0));
	}

	private async Task<JobNodeResult> FindChildNodeAsync(JobNodeId parentId, string description)
	{
		var children = await seedClient.Query.GetJobChildrenAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, ParentId = parentId },
			CancellationToken.None);
		var summary = children.Single(child => child.Description == description);

		var detail = await seedClient.Query.GetJobNodeAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, NodeId = summary.Id },
			CancellationToken.None);

		return detail.Node;
	}

	private async Task<HttpResponseMessage> PostAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId parentId, string description, AppUserId? ownerId, string? neededStart = null, AppUserId? beginWorkForUserId = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Create");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["ParentId"] = parentId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.Description"] = description,
			["Input.OwnerUserId"] = ownerId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
			["Input.BeginWorkForUserId"] = beginWorkForUserId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
			["Input.Priority"] = nameof(Priority.Medium),
			["__RequestVerificationToken"] = token,
		};
		if (neededStart is not null) {
			fields["Input.NeededStart"] = neededStart;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetCreateFormAsync(string authCookie, JobNodeId parentId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Create?parentId={parentId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Create page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Create page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}







	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("name=\"Input.BeginWorkForUserId\">(?<options>.*?)</select>", RegexOptions.Singleline)]
	private static partial Regex BeginWorkSelectPattern();





}
