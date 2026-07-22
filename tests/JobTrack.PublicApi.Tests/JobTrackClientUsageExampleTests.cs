namespace JobTrack.PublicApi.Tests;

using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Hierarchy;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;

/// <summary>
///     Plan §7.1's "compiling usage examples before creating implementations": this is genuine
///     consumer-shaped code against the real <see cref="IJobTrackClient" />/<see cref="IInstallationCommands" />
///     contracts declared in <c>JobTrack.Application</c>, using a throwaway in-memory test double in
///     place of a real persistence-backed implementation (none exists yet — this is a design-review
///     artifact, not a test of production behaviour). If the public surface's shape stops making sense
///     as consumer code, this file is the first thing that should hurt.
/// </summary>
public sealed class JobTrackClientUsageExampleTests
{
	[Fact]
	public void A_consumer_can_compose_each_provider_into_the_same_facade()
	{
		using var dataSource = NpgsqlDataSource.Create("Host=/tmp;Database=jobtrack-composition-example");

		var postgreSql = JobTrackPostgreSql.Create(dataSource);
		var sqlite = JobTrackSqlite.Create("Data Source=jobtrack-composition-example.db");

		postgreSql.Jobs.Should().NotBeNull();
		postgreSql.Query.Should().NotBeNull();
		sqlite.Jobs.Should().NotBeNull();
		sqlite.Query.Should().NotBeNull();
	}

	[Fact]
	public async Task A_consumer_bootstraps_the_first_administrator_through_one_configured_entry_point()
	{
		IJobTrackClient client = new FakeJobTrackClient();

		var result = await client.Installation.BootstrapAdministratorAsync(
			new() {
				DisplayName = "Ada Example",
				IanaTimeZone = "Europe/London",
				DefaultHourlyRate = new HourlyRate(25.00m),
				UserName = "ada.example",
				Password = "correct-horse-battery-staple",
				CorrelationId = Guid.NewGuid(),
			},
			CancellationToken.None);

		result.AdministratorId.Value.Should().BePositive();
		result.RootJobNodeId.Value.Should().BePositive();
	}

	[Fact]
	public async Task Bootstrapping_an_already_initialised_installation_throws_the_documented_exception()
	{
		IJobTrackClient client = new FakeJobTrackClient(true);

		var act = async () => await client.Installation.BootstrapAdministratorAsync(
			new() {
				DisplayName = "Ada Example",
				IanaTimeZone = "Europe/London",
				UserName = "ada.example",
				Password = "correct-horse-battery-staple",
				CorrelationId = Guid.NewGuid(),
			},
			CancellationToken.None);

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("installation-already-initialised");
	}

	[Fact]
	public async Task A_consumer_retrieves_their_own_employee_profile_through_one_configured_entry_point()
	{
		IJobTrackClient client = new FakeJobTrackClient();
		var actor = new AppUserId(1);

		var result = await client.Query.GetEmployeeProfileAsync(
			new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, TargetUserId = actor },
			CancellationToken.None);

		result.Id.Should().Be(actor);
	}

	[Fact]
	public async Task A_consumer_creates_a_child_node_through_one_configured_entry_point()
	{
		IJobTrackClient client = new FakeJobTrackClient();
		var actor = new AppUserId(1);

		var result = await client.Jobs.AddChildAsync(
			new() {
				Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() },
				ParentId = new(1),
				Description = "Do the thing",
				OwnerUserId = actor,
				Priority = Priority.Medium,
			},
			CancellationToken.None);

		result.Kind.Should().Be(NodeKind.Leaf);
	}

	private sealed class FakeJobTrackClient(bool alreadyInitialised = false) : IJobTrackClient
	{
		public IInstallationCommands Installation { get; } = new FakeInstallationCommands(alreadyInitialised);

		public IJobQueries Query { get; } = new FakeJobQueries();

		public IEmployeeCommands Employees { get; } = new FakeEmployeeCommands();

		public IJobCommands Jobs { get; } = new FakeJobCommands();

		public IWorkCommands Work { get; } = new FakeWorkCommands();

		public IScheduleCommands Schedules { get; } = new FakeScheduleCommands();

		public IRateCommands Rates { get; } = new FakeRateCommands();

		public ICostQueries Costs { get; } = new FakeCostQueries();

		public IAuditQueries Audit { get; } = new FakeAuditQueries();

		public ITokenCommands Tokens { get; } = new FakeTokenCommands();

		public IRequestCommands Requests { get; } = new FakeRequestCommands();

		public IAuthenticationAuditCommands AuthenticationAudit { get; } = new FakeAuthenticationAuditCommands();

		public IAccountCredentialCommands Credentials { get; } = new FakeAccountCredentialCommands();
	}

	private sealed class FakeAuthenticationAuditCommands : IAuthenticationAuditCommands
	{
		public Task RecordAsync(RecordAuthenticationAuditEventRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeAccountCredentialCommands : IAccountCredentialCommands
	{
		public Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
			SetTwoFactorStateRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeAuditQueries : IAuditQueries
	{
		public Task<AuditEventSearchResult> SearchAuditEventsAsync(
			AuditEventSearchRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeRequestCommands : IRequestCommands
	{
		public Task<JobRequestResult> SubmitAsync(SubmitJobRequestRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<JobNodeResult> MoveAsync(MoveRequesterJobRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<EquatableArray<JobRequestSummaryResult>> GetMyRequestsAsync(
			CommandContext context, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<EquatableArray<HoldingAreaSummaryResult>> GetEligibleHoldingAreasAsync(
			CommandContext context, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<JobRequestResult> AcknowledgeAsync(
			AcknowledgeJobRequestRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<JobRequestNoteResult> AddNoteAsync(AddJobRequestNoteRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<JobRequestDetailResult> GetDetailAsync(
			GetJobRequestDetailRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeTokenCommands : ITokenCommands
	{
		public Task<IssuedPersonalAccessTokenResult> IssueAsync(
			IssuePersonalAccessTokenRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<EquatableArray<PersonalAccessTokenSummaryResult>> ListAsync(
			ListPersonalAccessTokensRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task RevokeAsync(RevokePersonalAccessTokenRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task RevokeAllAsync(RevokeAllPersonalAccessTokensRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<AuthenticatedPersonalAccessTokenResult?> TryAuthenticateAsync(
			TryAuthenticatePersonalAccessTokenRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeCostQueries : ICostQueries
	{
		public Task<CostDetailsResult> GetCostDetailsAsync(
			GetCostDetailsRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<HierarchyTotalsResult> GetHierarchyTotalsAsync(
			GetHierarchyTotalsRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<BulkNodeCostResult> GetBulkNodeCostsAsync(
			GetBulkNodeCostsRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeRateCommands : IRateCommands
	{
		public Task<UserCostRateResult> AddUserCostRateAsync(
			AddUserCostRateRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<NodeRateOverrideResult> AddNodeRateOverrideAsync(
			AddNodeRateOverrideRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<UserCostRateResult> CorrectUserCostRateAsync(
			CorrectUserCostRateRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<NodeRateOverrideResult> CorrectNodeRateOverrideAsync(
			CorrectNodeRateOverrideRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeEmployeeCommands : IEmployeeCommands
	{
		public Task<AccountStateResult> CreateEmployeeAsync(
			CreateEmployeeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<EmployeeRolesResult> AssignRoleAsync(
			AssignEmployeeRoleRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<EmployeeRolesResult> RevokeRoleAsync(
			RevokeEmployeeRoleRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<AccountStateResult> SetEnabledAsync(
			SetEmployeeEnabledRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<EmployeeProfileResult> SetDefaultHourlyRateAsync(
			SetEmployeeDefaultHourlyRateRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<AccountStateResult> ResetPasswordAsync(
			ResetEmployeePasswordRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<AccountStateResult> ResetTwoFactorAsync(
			ResetEmployeeTwoFactorRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<EmployeeProfileResult> SetHomeNodeAsync(
			SetHomeNodeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeScheduleCommands : IScheduleCommands
	{
		public Task<ScheduleVersionResult> AddScheduleVersionAsync(
			AddScheduleVersionRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<ScheduleExceptionResult> AddScheduleExceptionAsync(
			AddScheduleExceptionRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<ScheduleVersionResult> CorrectScheduleVersionAsync(
			CorrectScheduleVersionRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<ScheduleExceptionResult> CorrectScheduleExceptionAsync(
			CorrectScheduleExceptionRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeWorkCommands : IWorkCommands
	{
		public Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(
			ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeJobCommands : IJobCommands
	{
		public Task<JobNodeResult> AddChildAsync(CreateJobNodeRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(CreateResult(request));

		public Task<JobNodeResult> EditAsync(EditJobNodeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<JobNodeResult> MoveAsync(MoveJobNodeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<JobNodeResult> ArchiveAsync(ArchiveJobNodeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task DeleteAsync(DeleteJobNodeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<LeafWorkResult> AttachLeafWorkAsync(AttachLeafWorkRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<DecomposeWorkedLeafResult> DecomposeWorkedLeafAsync(
			DecomposeWorkedLeafRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task AddPrerequisiteAsync(AddPrerequisiteRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task RemovePrerequisiteAsync(RemovePrerequisiteRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<JobNodeResult> PickUpAsync(PickUpJobNodeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		public Task<ImportSubtreeResult> ImportSubtreeAsync(ImportSubtreeRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");

		private static JobNodeResult CreateResult(CreateJobNodeRequest request) => new() {
			Id = new(2),
			ParentId = request.ParentId,
			Kind = NodeKind.Leaf,
			Description = request.Description,
			WriteUp = request.WriteUp,
			PostedByUserId = request.Context.Actor,
			OwnerUserId = request.OwnerUserId,
			ExpectedDurationHours = request.ExpectedDurationHours,
			ExpectedCost = request.ExpectedCost,
			NeededStart = request.NeededStart,
			NeededFinish = request.NeededFinish,
			Priority = request.Priority,
			PostedAt = Instant.FromUtc(2026, 7, 5, 0, 0),
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		};
	}

	private sealed class FakeJobQueries : IJobQueries
	{
		public Task<EmployeeProfileResult> GetEmployeeProfileAsync(
			GetEmployeeProfileRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new EmployeeProfileResult {
				Id = request.TargetUserId,
				DisplayName = "Ada Example",
				IanaTimeZone = "Europe/London",
				DefaultHourlyRate = new HourlyRate(25.00m),
				Version = 1,
			});

		public Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryAsync(
			GetEmployeeDirectoryRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<EmployeeDirectoryEntry>>([
				new() { Id = new(1), DisplayName = "Ada Example", UserName = "ada.example" },
			]);

		public Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesAsync(
			GetAllEmployeesRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<EmployeeDirectoryEntry>>([
				new() { Id = new(1), DisplayName = "Ada Example", UserName = "ada.example" },
			]);

		public Task<AccountStateResult> GetAccountStateAsync(
			GetAccountStateRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new AccountStateResult {
				Id = request.TargetUserId,
				UserName = "ada.example",
				IsEnabled = true,
				RequiresPasswordChange = false,
				Roles = [EmployeeRole.Administrator],
			});

		public Task<ReadinessResult> GetReadinessAsync(GetReadinessRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new ReadinessResult(true, []));

		public Task<JobNodeDetailResult> GetJobNodeAsync(GetJobNodeRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new JobNodeDetailResult {
				Node = new() {
					Id = request.NodeId ?? new JobNodeId(1),
					ParentId = null,
					Kind = NodeKind.Root,
					Description = "Example root",
					PostedByUserId = new(1),
					OwnerUserId = new AppUserId(1),
					Priority = Priority.Medium,
					PostedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
					HasChildren = false,
					HasLeafWork = false,
					Version = 1,
				},
				Ancestors = [],
			});

		public Task<EquatableArray<JobNodeSummaryResult>> GetJobChildrenAsync(
			GetJobChildrenRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<JobNodeSummaryResult>>([]);

		public Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesAsync(
			SearchJobNodesRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<JobNodeSummaryResult>>([]);

		public Task<EquatableArray<JobNodeSummaryResult>> GetJobSummariesAsync(
			GetJobSummariesRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<JobNodeSummaryResult>>([]);

		public Task<JobSubtreeResult> GetJobSubtreeAsync(
			GetJobSubtreeRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new JobSubtreeResult { RootId = request.RootId, Nodes = [] });

		public Task<EquatableArray<AwaitingProgressEntry>> GetAwaitingProgressAsync(
			GetAwaitingProgressRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<AwaitingProgressEntry>>([]);

		public Task<EquatableArray<WorkSessionResult>> GetLeafSessionsAsync(
			GetLeafSessionsRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<WorkSessionResult>>([]);

		public Task<EquatableArray<WorkSessionResult>> GetActiveSessionsAsync(
			GetActiveSessionsRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<WorkSessionResult>>([]);

		public Task<EquatableArray<LeafSessionManageCapabilityResult>> GetSessionManageCapabilitiesAsync(
			GetSessionManageCapabilitiesRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<LeafSessionManageCapabilityResult>>([]);

		public Task<LeafWorkResult> GetLeafWorkAsync(GetLeafWorkRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new LeafWorkResult {
				JobNodeId = request.JobNodeId,
				Achievement = Achievement.Waiting,
				ChangedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
				Version = 1,
			});

		public Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesAsync(
			GetPrerequisitesRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult<EquatableArray<PrerequisiteEdge>>([]);

		public Task<ScheduleSnapshotResult> GetScheduleAsync(GetScheduleRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new ScheduleSnapshotResult { Versions = [], Exceptions = [] });

		public Task<RateSnapshotResult> GetRatesAsync(GetRatesRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new RateSnapshotResult { UserCostRates = [], NodeRateOverrides = [] });

		public Task<LeafWorkPageResult> GetLeafWorkPageAsync(GetLeafWorkPageRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Not exercised by this usage example.");
	}

	private sealed class FakeInstallationCommands(bool alreadyInitialised) : IInstallationCommands
	{
		public Task<BootstrapAdministratorResult> BootstrapAdministratorAsync(
			BootstrapAdministratorRequest request, CancellationToken cancellationToken = default)
		{
			if (alreadyInitialised) {
				throw new InvariantViolationException("installation-already-initialised", "The installation is already initialised.");
			}

			return Task.FromResult(new BootstrapAdministratorResult {
				AdministratorId = new(1),
				AdministratorVersion = 1,
				RootJobNodeId = new(1),
				RootVersion = 1,
				InitializedAt = Instant.FromUtc(2026, 7, 5, 0, 0),
			});
		}
	}
}
