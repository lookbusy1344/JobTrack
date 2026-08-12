namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Schedules;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Direct-HTTP tests for the external HTTP API's Browse subtree surface (plan §4.3, ADR 0039/0040):
///     <c>GET /api/jobs/{nodeId}/subtree</c>. Structure is <c>AnyEmployee</c>-gated (no ownership
///     check), unlike the cost roll-up, which is individually omitted per ADR 0040 rather than denying
///     the whole request.
/// </summary>
public sealed partial class JobSubtreeApiTests : IAsyncLifetime, IDisposable
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
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.subtree-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
		rootId = bootstrap.RootJobNodeId;

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
	public async Task Any_employee_can_browse_the_subtree_structure_without_cost_permission()
	{
		var (workerId, branchId, leafId) = await SeedBranchWithLeafAsync("subtree.structure.worker");
		var authCookie = await client.SignInAsync("subtree.structure.worker");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{rootId.Value}/subtree?asOf=2026-01-02T00:00:00%2B00:00", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("rootId").GetInt64().Should().Be(rootId.Value);
		jsonDocument.RootElement.GetProperty("rootAchievement").GetString().Should().Be(nameof(BranchAchievement.Unfinished));
		jsonDocument.RootElement.GetProperty("rootTotal").ValueKind.Should().Be(JsonValueKind.Null);
		jsonDocument.RootElement.GetProperty("rootAllocatedHours").ValueKind.Should().Be(JsonValueKind.Null);
		var nodes = jsonDocument.RootElement.GetProperty("nodes").EnumerateArray().ToList();
		nodes.Should().Contain(node => node.GetProperty("id").GetInt64() == branchId.Value);
		nodes.Should().Contain(node => node.GetProperty("id").GetInt64() == leafId.Value);
		nodes.Should().OnlyContain(node => node.GetProperty("cost").ValueKind == JsonValueKind.Null);
		nodes.Should().OnlyContain(node => node.GetProperty("allocatedHours").ValueKind == JsonValueKind.Null);
	}

	/// <summary>
	///     ADR 0040's carve-out is checked against the requested subtree <em>root</em>'s ownership chain,
	///     not any descendant's -- so this browses the subtree rooted at the leaf the worker owns
	///     directly, not the permanent root (which only the administrator owns).
	/// </summary>
	[Fact]
	public async Task A_node_owner_sees_the_cost_roll_up_without_a_cost_viewing_role()
	{
		var (workerId, _, leafId) = await SeedBranchWithLeafAsync("subtree.owner-cost.worker");
		var authCookie = await client.SignInAsync("subtree.owner-cost.worker");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{leafId.Value}/subtree?asOf=2026-01-02T00:00:00%2B00:00", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("rootAchievement").ValueKind.Should().Be(JsonValueKind.Null);
		jsonDocument.RootElement.GetProperty("rootTotal").ValueKind.Should().NotBe(JsonValueKind.Null);
		jsonDocument.RootElement.GetProperty("rootAllocatedHours").GetDecimal().Should().Be(8m);
		var leafNode = jsonDocument.RootElement.GetProperty("nodes").EnumerateArray()
								   .Single(node => node.GetProperty("id").GetInt64() == leafId.Value);
		leafNode.GetProperty("cost").ValueKind.Should().NotBe(JsonValueKind.Null);
		leafNode.GetProperty("allocatedHours").GetDecimal().Should().Be(8m);
	}

	[Fact]
	public async Task Rejects_a_depth_beyond_the_hard_cap()
	{
		_ = await SeedBranchWithLeafAsync("subtree.depth-limit.worker");
		var authCookie = await client.SignInAsync("subtree.depth-limit.worker");

		var response = await client.GetAuthenticatedAsync(
			$"/api/jobs/{rootId.Value}/subtree?asOf=2026-01-02T00:00:00%2B00:00&depth=6", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Returns_not_found_for_a_nonexistent_root()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "subtree.missing-root.worker");
		var authCookie = await client.SignInAsync("subtree.missing-root.worker");

		var response = await client.GetAuthenticatedAsync("/api/jobs/999999/subtree?asOf=2026-01-02T00:00:00%2B00:00", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	private async Task<(AppUserId WorkerId, JobNodeId BranchId, JobNodeId LeafId)> SeedBranchWithLeafAsync(string workerUserName)
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, workerUserName);
		var branch = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Kitchen renovation",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = branch.Id,
			Description = "Fit cabinets",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
		});
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 18, 0)),
				null),
			Reason = "Full working window for subtree API tests",
		});
		_ = await seedClient.Rates.AddUserCostRateAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = Instant.FromUtc(2026, 1, 1, 17, 0),
		});

		return (workerId, branch.Id, leaf.Id);
	}









	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();
}
