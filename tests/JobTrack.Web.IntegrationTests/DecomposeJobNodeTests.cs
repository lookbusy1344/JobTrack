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
///     Direct-HTTP tests for the job-node decompose workflow (plan §8.5 slice 3, spec §3.5, ADR 0067):
///     atomic conversion of a worked leaf, or a bare leaf with no attached <c>LeafWork</c>, into a
///     branch, plus concurrency-conflict recovery and rejection of a node that already has children.
/// </summary>
public sealed partial class DecomposeJobNodeTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const int NewChildSlotCount = 5;

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
			UserName = "admin.decompose-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;
		administratorId = bootstrapResult.AdministratorId;

		factory = new(database.ConnectionString);
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
	public async Task A_job_manager_can_save_decomposing_a_worked_leaf()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Build deck");
		var authCookie = await client.SignInAsync("decompose.manager");

		var (antiforgeryCookie, token) = await GetDecomposeFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, managerId,
			"Deck project", "Existing framing work", "Add railings");

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var afterSave = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leaf.Id.Value}", authCookie);
		var afterBody = await afterSave.Content.ReadAsStringAsync();
		afterBody.Should().Contain("Deck project");
		afterBody.Should().Contain("Existing framing work");
		afterBody.Should().Contain("Add railings");
	}

	[Fact]
	public async Task The_decompose_page_offers_save_and_cancel_actions()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.actions", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Deck to split");
		var authCookie = await client.SignInAsync("decompose.actions");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Decompose?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(">Save<");
		body.Should().Contain(">Cancel<");
		body.Should().NotContain("Preview");
		body.Should().NotContain("Confirm decomposition");
	}

	[Fact]
	public async Task A_job_manager_can_create_an_unassigned_new_child_while_decomposing()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.unassigned-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Split pool work");
		var authCookie = await client.SignInAsync("decompose.unassigned-manager");

		var (antiforgeryCookie, token) = await GetDecomposeFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, null,
			"Pool branch", "Existing pool work", "Unassigned decomposed child");

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var browseResponse = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={leaf.Id.Value}&unassignedOnly=true", authCookie);
		var browseBody = await browseResponse.Content.ReadAsStringAsync();
		browseBody.Should().Contain("Unassigned decomposed child");
	}

	[Fact]
	public async Task A_worker_who_does_not_own_the_leaf_is_denied_on_save()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.owner-manager", EmployeeRole.JobManager);
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.denied-worker");
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Owned by manager");
		var authCookie = await client.SignInAsync("decompose.denied-worker");

		var (antiforgeryCookie, token) = await GetDecomposeFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, workerId,
			"Hijacked branch", "Hijacked existing work", "");

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	/// <summary>
	///     ADR 0067: a bare leaf (no <c>LeafWork</c> attached) has nothing to carry over, but it can
	///     still be decomposed — the form offers only the branch description and the new-children slots,
	///     with no "what moves onto the new child" section since nothing does.
	/// </summary>
	[Fact]
	public async Task The_decompose_page_offers_a_form_for_a_bare_leaf()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.bare-form-manager", EmployeeRole.JobManager);
		var bareLeaf = await AddBareLeafAsync(managerId, "Bare leaf, no work attached");
		var authCookie = await client.SignInAsync("decompose.bare-form-manager");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Decompose?leafNodeId={bareLeaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(">Save<");
		body.Should().Contain("name=\"Input.BranchDescription\"");
		body.Should().NotContain("name=\"Input.ExistingWorkDescription\"");
		body.Should().NotContain("What moves onto the new child");
	}

	[Fact]
	public async Task Decomposing_a_bare_leaf_creates_the_listed_children()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.bare-manager", EmployeeRole.JobManager);
		var bareLeaf = await AddBareLeafAsync(managerId, "Bare leaf, no work attached");
		var authCookie = await client.SignInAsync("decompose.bare-manager");

		var (antiforgeryCookie, token) = await GetDecomposeFormAsync(authCookie, bareLeaf.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, bareLeaf.Id, bareLeaf.Version, managerId,
			"Umbrella branch", "", "Named child of the bare leaf");

		saveResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		saveResponse.Headers.Location!.OriginalString.Should().Contain("/Jobs/Browse");

		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = bareLeaf.Id,
			}, CancellationToken.None);
		current.Node.HasChildren.Should().BeTrue();
		current.Node.HasLeafWork.Should().BeFalse();

		var afterSave = await client.GetAuthenticatedAsync($"/Jobs/Browse?nodeId={bareLeaf.Id.Value}", authCookie);
		var afterBody = await afterSave.Content.ReadAsStringAsync();
		afterBody.Should().Contain("Named child of the bare leaf");
	}

	[Fact]
	public async Task Decomposing_a_bare_leaf_with_no_named_children_shows_a_validation_error()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.bare-empty-manager", EmployeeRole.JobManager);
		var bareLeaf = await AddBareLeafAsync(managerId, "Bare leaf, no work attached");
		var authCookie = await client.SignInAsync("decompose.bare-empty-manager");

		var (antiforgeryCookie, token) = await GetDecomposeFormAsync(authCookie, bareLeaf.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, bareLeaf.Id, bareLeaf.Version, managerId,
			"Umbrella branch", "", "");
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("Name at least one new child");

		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = bareLeaf.Id,
			}, CancellationToken.None);
		current.Node.HasChildren.Should().BeFalse("the decompose must not have run");
	}

	[Fact]
	public async Task The_decompose_page_refuses_a_node_with_children()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.branch-manager", EmployeeRole.JobManager);
		var branch = await AddBareLeafAsync(managerId, "Already a branch");
		_ = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = branch.Id,
			Description = "Existing child",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
		});
		var authCookie = await client.SignInAsync("decompose.branch-manager");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Decompose?leafNodeId={branch.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("Only a leaf can be decomposed.");
		body.Should().NotContain(">Save<");
		body.Should().NotContain("name=\"Input.BranchDescription\"");
	}

	/// <summary>
	///     The page's whole reason for existing beyond the two description fields: a reader must be able
	///     to see, before saving, that the work does not stay put — a new child is created to inherit the
	///     achievement and every session, running clocks included.
	/// </summary>
	[Fact]
	public async Task The_decompose_page_says_a_new_child_inherits_the_work_and_names_the_running_session()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.explains-manager", EmployeeRole.JobManager);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Leaf under way",
			OwnerUserId = managerId,
			Priority = Priority.Medium,
			BeginWork = new() {
				WorkedByUserId = managerId,
			},
		});
		var authCookie = await client.SignInAsync("decompose.explains-manager");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Decompose?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("What moves onto the new child");
		body.Should().Contain("becomes a branch");
		body.Should().Contain("<strong>In Progress</strong>");
		body.Should().Contain("<strong>one still running</strong>");
	}

	/// <summary>
	///     A decomposition splits one person's job into the pieces it turned out to need, so every new
	///     child slot starts on that person — the same owner the existing-work child inherits in the
	///     command — rather than dropping the pieces into the unassigned pool by default.
	/// </summary>
	[Fact]
	public async Task Every_new_child_slot_defaults_its_owner_to_the_decomposed_nodes_owner()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.default-owner-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Owned work to split");
		var authCookie = await client.SignInAsync("decompose.default-owner-manager");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Decompose?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var selectedOwner = $"<option selected=\"selected\" value=\"{managerId.Value.ToString(CultureInfo.InvariantCulture)}\">";
		CountOccurrences(body, selectedOwner).Should().Be(NewChildSlotCount);
	}

	[Fact]
	public async Task An_unassigned_nodes_new_child_slots_stay_unassigned()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.pool-owner-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, null, "Pool work to split");
		var authCookie = await client.SignInAsync("decompose.pool-owner-manager");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Decompose?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		// Nothing is pre-selected, so each owner select falls back to its first option — "Unassigned".
		CountOccurrences(body, ".OwnerUserId\"><option value=\"\">Unassigned</option>").Should().Be(NewChildSlotCount);
		CountOccurrences(body, $"<option selected=\"selected\" value=\"{managerId.Value.ToString(CultureInfo.InvariantCulture)}\">")
			.Should().Be(0);
	}

	private static int CountOccurrences(string body, string value)
	{
		var count = 0;
		for (var index = body.IndexOf(value, StringComparison.Ordinal);
			 index >= 0;
			 index = body.IndexOf(value, index + value.Length, StringComparison.Ordinal)) {
			++count;
		}

		return count;
	}

	private async Task<JobNodeResult> AddBareLeafAsync(AppUserId ownerId, string description) =>
		await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

	[Fact]
	public async Task A_stale_version_on_save_is_reported_as_a_conflict()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.conflict-manager", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Contested leaf");
		var authCookie = await client.SignInAsync("decompose.conflict-manager");

		var (antiforgeryCookie, token) = await GetDecomposeFormAsync(authCookie, leaf.Id);

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

		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, managerId,
			"Branch", "Existing work", "");
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("Someone else changed this node");
	}

	/// <summary>
	///     §2.4: a malformed <c>NeededStart</c> on a new child slot is rejected before the atomic
	///     decompose command runs, even though the current form doesn't render that field — a raw HTTP
	///     POST can still supply it.
	/// </summary>
	[Fact]
	public async Task A_malformed_new_child_NeededStart_is_rejected_without_decomposing()
	{
		var managerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "decompose.malformed-needed", EmployeeRole.JobManager);
		var leaf = await AddWorkedLeafAsync(rootId, managerId, "Leaf with malformed child needed-start");
		var authCookie = await client.SignInAsync("decompose.malformed-needed");

		var (antiforgeryCookie, token) = await GetDecomposeFormAsync(authCookie, leaf.Id);
		var saveResponse = await PostAsync(
			authCookie, antiforgeryCookie, token, leaf.Id, leaf.Version, managerId,
			"Deck project", "Existing framing work", "Add railings", "not-a-local-date-time");
		var saveBody = await saveResponse.Content.ReadAsStringAsync();

		saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		saveBody.Should().Contain("Enter a valid date and time");

		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			}, CancellationToken.None);
		current.Node.HasChildren.Should().BeFalse("the decompose must not have run");
	}

	private async Task<JobNodeResult> AddWorkedLeafAsync(JobNodeId parentId, AppUserId? ownerId, string description)
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

	private async Task<HttpResponseMessage> PostAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId leafNodeId, long version, AppUserId? newChildOwnerId,
		string branchDescription, string existingWorkDescription, string firstNewChildDescription,
		string? firstNewChildNeededStart = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Decompose");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var form = new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["OriginalVersion"] = version.ToString(CultureInfo.InvariantCulture),
			["Input.BranchDescription"] = branchDescription,
			["Input.ExistingWorkDescription"] = existingWorkDescription,
			["__RequestVerificationToken"] = token,
		};

		for (var i = 0; i < NewChildSlotCount; ++i) {
			form[$"Input.NewChildren[{i}].Description"] = i == 0 ? firstNewChildDescription : string.Empty;
			form[$"Input.NewChildren[{i}].OwnerUserId"] = newChildOwnerId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
			form[$"Input.NewChildren[{i}].Priority"] = nameof(Priority.Medium);
		}

		if (firstNewChildNeededStart is not null) {
			form["Input.NewChildren[0].NeededStart"] = firstNewChildNeededStart;
		}

		request.Content = new FormUrlEncodedContent(form);
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetDecomposeFormAsync(string authCookie, JobNodeId leafNodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Decompose?leafNodeId={leafNodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Decompose page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Decompose page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}







	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();
}
