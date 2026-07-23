namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Hierarchy;

/// <summary>
///     Shared TC-DB-REQ-002/TC-DB-REQ-003 contract for <see cref="IJobRequestCommandPort.SubmitAsync" />
///     and <see cref="IJobRequestCommandPort.MoveAsync" /> (ADR 0033), asserted identically against
///     PostgreSQL and SQLite by one thin sealed subclass per provider's own test project — same shape as
///     <see cref="JobNodeCommandPortContractTestsBase" />.
/// </summary>
public abstract class JobRequestCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private readonly IDisposableTestDatabase database;

	protected JobRequestCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_requester_can_submit_a_request_into_an_eligible_active_holding_area()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);

		var result = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		result.JobNodeId.Value.Should().BePositive();
		result.RequesterUserId.Should().Be(requesterId);
		result.HoldingAreaId.Should().Be(holdingAreaId);
		result.OwnerUserId.Should().BeNull();
		result.Description.Should().Be("Printer will not turn on");
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task Submitting_a_request_writes_an_audit_event()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);

		var result = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_request", EntityId = result.JobNodeId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle();
		audit.Events[0].Operation.Should().Be("submit-request");
		audit.Events[0].ActorId.Should().Be(requesterId);
	}

	[Fact]
	public async Task The_default_owner_configured_on_the_holding_area_is_applied_to_the_new_request()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var staffId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper", EmployeeRole.Worker);
		var holdingAreaId = await SeedHoldingAreaAsync(null, staffId, true);
		var port = CreateCommandPort(database.ConnectionString);

		var result = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		result.OwnerUserId.Should().Be(staffId);
	}

	[Fact]
	public async Task A_requester_cannot_submit_into_an_inactive_holding_area()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, false);
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_requester_cannot_submit_into_a_holding_area_scoped_to_a_department_they_do_not_belong_to()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var departmentId = await SeedDepartmentAsync("IT Support");
		var holdingAreaId = await SeedHoldingAreaAsync(departmentId, null, true);
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_requester_can_submit_into_a_department_scoped_holding_area_they_belong_to()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var departmentId = await SeedDepartmentAsync("IT Support");
		await SeedAppUserDepartmentAsync(requesterId, departmentId);
		var holdingAreaId = await SeedHoldingAreaAsync(departmentId, null, true);
		var port = CreateCommandPort(database.ConnectionString);

		var result = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		result.JobNodeId.Value.Should().BePositive();
	}

	[Fact]
	public async Task A_non_requester_role_cannot_submit_a_request()
	{
		await SeedRootAndRequesterAsync();
		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper", EmployeeRole.Worker);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.SubmitAsync(SubmitRequest(workerId, holdingAreaId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Submitting_to_a_nonexistent_holding_area_throws_not_found()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.SubmitAsync(SubmitRequest(requesterId, new(999_999)));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_disabled_requester_cannot_submit_a_request()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		await SetActorAccountStateAsync(requesterId, false, null);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_job_manager_can_move_a_requester_job_to_a_new_parent_without_owning_it()
	{
		var (rootId, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var strangerId = await SeedEmployeeAsync("Sam Stranger", "sam.stranger", EmployeeRole.Worker);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var destinationId = await InsertNodeAsync(rootId.Value, strangerId, "Destination branch");
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var moved = await port.MoveAsync(MoveRequest(jobManagerId, submitted.JobNodeId, new(destinationId), submitted.Version));

		moved.ParentId.Should().Be(new JobNodeId(destinationId));
	}

	[Fact]
	public async Task The_assigned_owner_of_a_requester_job_can_move_it_without_owning_the_destination_parent()
	{
		var (rootId, requesterId) = await SeedRootAndRequesterAsync();
		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper", EmployeeRole.Worker);
		var strangerId = await SeedEmployeeAsync("Sam Stranger", "sam.stranger", EmployeeRole.Worker);
		var holdingAreaId = await SeedHoldingAreaAsync(null, workerId, true);
		var destinationId = await InsertNodeAsync(rootId.Value, strangerId, "Destination branch");
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var moved = await port.MoveAsync(MoveRequest(workerId, submitted.JobNodeId, new(destinationId), submitted.Version));

		moved.ParentId.Should().Be(new JobNodeId(destinationId));
	}

	[Fact]
	public async Task An_owner_of_an_ancestor_of_a_requester_job_can_move_it_without_owning_the_destination_parent()
	{
		var (rootId, requesterId) = await SeedRootAndRequesterAsync();
		var ancestorOwnerId = await SeedEmployeeAsync("Ada Ancestor", "ada.ancestor", EmployeeRole.Worker);
		var strangerId = await SeedEmployeeAsync("Sam Stranger", "sam.stranger", EmployeeRole.Worker);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		await SetHoldingAreaNodeOwnerAsync(holdingAreaId, ancestorOwnerId);
		var destinationId = await InsertNodeAsync(rootId.Value, strangerId, "Destination branch");
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var moved = await port.MoveAsync(
			MoveRequest(ancestorOwnerId, submitted.JobNodeId, new(destinationId), submitted.Version));

		moved.ParentId.Should().Be(new JobNodeId(destinationId));
	}

	[Fact]
	public async Task A_worker_who_does_not_control_the_requester_job_cannot_move_it()
	{
		var (rootId, requesterId) = await SeedRootAndRequesterAsync();
		var strangerId = await SeedEmployeeAsync("Sam Stranger", "sam.stranger", EmployeeRole.Worker);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var destinationId = await InsertNodeAsync(rootId.Value, strangerId, "Destination branch");
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var act = () => port.MoveAsync(MoveRequest(strangerId, submitted.JobNodeId, new(destinationId), submitted.Version));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Moving_a_requester_job_preserves_the_job_request_anchor()
	{
		var (rootId, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var destinationId = await InsertNodeAsync(rootId.Value, jobManagerId, "Destination branch");
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		_ = await port.MoveAsync(MoveRequest(jobManagerId, submitted.JobNodeId, new(destinationId), submitted.Version));

		var (anchoredHoldingAreaId, anchoredRequesterId) = await ReadJobRequestAnchorAsync(submitted.JobNodeId);
		anchoredHoldingAreaId.Should().Be(holdingAreaId);
		anchoredRequesterId.Should().Be(requesterId);
	}

	[Fact]
	public async Task Moving_an_ordinary_job_node_without_a_job_request_row_is_rejected()
	{
		var (rootId, _) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var ordinaryNodeId = await InsertNodeAsync(rootId.Value, jobManagerId, "Ordinary node");
		var destinationId = await InsertNodeAsync(rootId.Value, jobManagerId, "Destination branch");
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.MoveAsync(MoveRequest(jobManagerId, new(ordinaryNodeId), new(destinationId), 1));

		await act.Should().ThrowAsync<InvariantViolationException>();
	}

	[Fact]
	public async Task Moving_a_requester_job_to_a_descendant_of_itself_is_rejected_as_a_cycle()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		var childOfRequestId = await InsertNodeAsync(submitted.JobNodeId.Value, jobManagerId, "Child of the request");

		var act = () => port.MoveAsync(
			MoveRequest(jobManagerId, submitted.JobNodeId, new(childOfRequestId), submitted.Version));

		await act.Should().ThrowAsync<InvariantViolationException>();
	}

	[Fact]
	public async Task Moving_a_requester_job_with_a_stale_version_is_rejected_as_a_conflict()
	{
		var (rootId, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var destinationId = await InsertNodeAsync(rootId.Value, jobManagerId, "Destination branch");
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var act = () => port.MoveAsync(
			MoveRequest(jobManagerId, submitted.JobNodeId, new(destinationId), submitted.Version + 1));

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task A_requester_sees_only_their_own_submitted_requests_most_recent_first()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var otherRequesterId = await SeedEmployeeAsync("Ravi Requester", "ravi.requester", EmployeeRole.Requester);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var first = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		var second = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		_ = await port.SubmitAsync(SubmitRequest(otherRequesterId, holdingAreaId));

		var mine = await port.GetMyRequestsAsync(new() { Actor = requesterId, CorrelationId = Guid.NewGuid() });

		mine.Select(r => r.JobNodeId).Should().Equal(second.JobNodeId, first.JobNodeId);
	}

	[Fact]
	public async Task A_requester_with_no_submitted_requests_sees_an_empty_list()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var mine = await port.GetMyRequestsAsync(new() { Actor = requesterId, CorrelationId = Guid.NewGuid() });

		mine.Should().BeEmpty();
	}

	[Fact]
	public async Task Eligible_holding_areas_include_globally_eligible_ones_and_exclude_inactive_or_unrelated_department_ones()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var globalId = await SeedHoldingAreaAsync(null, null, true);
		_ = await SeedHoldingAreaAsync(null, null, false);
		var otherDepartmentId = await SeedDepartmentAsync("HR");
		_ = await SeedHoldingAreaAsync(otherDepartmentId, null, true);

		var eligible = await port.GetEligibleHoldingAreasAsync(new() { Actor = requesterId, CorrelationId = Guid.NewGuid() });

		eligible.Select(h => h.Id).Should().Equal(globalId);
	}

	[Fact]
	public async Task Eligible_holding_areas_include_ones_scoped_to_a_department_the_actor_belongs_to()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var departmentId = await SeedDepartmentAsync("IT Support");
		await SeedAppUserDepartmentAsync(requesterId, departmentId);
		var scopedId = await SeedHoldingAreaAsync(departmentId, null, true);
		var port = CreateCommandPort(database.ConnectionString);

		var eligible = await port.GetEligibleHoldingAreasAsync(new() { Actor = requesterId, CorrelationId = Guid.NewGuid() });

		eligible.Select(h => h.Id).Should().Equal(scopedId);
	}

	[Fact]
	public async Task A_job_manager_can_acknowledge_a_requester_job()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var acknowledged = await port.AcknowledgeAsync(AcknowledgeRequest(jobManagerId, submitted.JobNodeId, submitted.Version));

		acknowledged.AcknowledgedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Acknowledging_a_request_writes_an_audit_event()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		_ = await port.AcknowledgeAsync(AcknowledgeRequest(jobManagerId, submitted.JobNodeId, submitted.Version));

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_request", EntityId = submitted.JobNodeId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().Contain(e => e.Operation == "acknowledge-request");
	}

	[Fact]
	public async Task A_worker_who_does_not_control_the_requester_job_cannot_acknowledge_it()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var strangerId = await SeedEmployeeAsync("Sam Stranger", "sam.stranger", EmployeeRole.Worker);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var act = () => port.AcknowledgeAsync(AcknowledgeRequest(strangerId, submitted.JobNodeId, submitted.Version));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Acknowledging_a_request_with_a_stale_version_is_rejected_as_a_conflict()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var act = () => port.AcknowledgeAsync(AcknowledgeRequest(jobManagerId, submitted.JobNodeId, submitted.Version + 1));

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Acknowledging_a_request_a_second_time_is_rejected()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var acknowledged = await port.AcknowledgeAsync(AcknowledgeRequest(jobManagerId, submitted.JobNodeId, submitted.Version));

		var act = () => port.AcknowledgeAsync(AcknowledgeRequest(jobManagerId, submitted.JobNodeId, acknowledged.Version));

		var exception = await act.Should().ThrowAsync<InvariantViolationException>();
		exception.Which.ConstraintId.Should().Be("request-already-acknowledged");
	}

	[Fact]
	public async Task Staff_can_add_a_private_note_not_visible_to_the_requester()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var note = await port.AddNoteAsync(AddNoteRequest(jobManagerId, submitted.JobNodeId, "Triage: waiting on parts", false));

		note.VisibleToRequester.Should().BeFalse();
	}

	[Fact]
	public async Task Staff_can_add_a_requester_visible_note()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var note = await port.AddNoteAsync(AddNoteRequest(jobManagerId, submitted.JobNodeId, "We are on it", true));

		note.VisibleToRequester.Should().BeTrue();
	}

	[Fact]
	public async Task The_requester_can_add_a_note_that_is_always_visible_to_the_requester()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var note = await port.AddNoteAsync(AddNoteRequest(requesterId, submitted.JobNodeId, "Any update?", false));

		note.VisibleToRequester.Should().BeTrue();
	}

	[Fact]
	public async Task A_different_requester_cannot_add_a_note_to_someone_elses_request()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var otherRequesterId = await SeedEmployeeAsync("Ravi Requester", "ravi.requester", EmployeeRole.Requester);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var act = () => port.AddNoteAsync(AddNoteRequest(otherRequesterId, submitted.JobNodeId, "Not mine", true));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_note_writes_an_audit_event()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var note = await port.AddNoteAsync(AddNoteRequest(requesterId, submitted.JobNodeId, "Any update?", true));

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_request_note", EntityId = note.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle();
		audit.Events[0].Operation.Should().Be("add-request-note");
	}

	[Fact]
	public async Task The_requester_can_view_their_own_request_detail_including_status_and_subtree()
	{
		var (rootId, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		var childId = await InsertNodeAsync(submitted.JobNodeId.Value, jobManagerId, "Sub-job created by triage");

		var detail = await port.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));

		detail.JobNodeId.Should().Be(submitted.JobNodeId);
		detail.Status.Should().Be(RequesterStatus.Submitted);
		detail.Subtree.Select(n => n.JobNodeId).Should().Contain([submitted.JobNodeId, new(childId)]);
		_ = rootId;
	}

	[Fact]
	public async Task Requesting_detail_reflects_acknowledged_status()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		_ = await port.AcknowledgeAsync(AcknowledgeRequest(jobManagerId, submitted.JobNodeId, submitted.Version));

		var detail = await port.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));

		detail.Status.Should().Be(RequesterStatus.Accepted);
		detail.AcknowledgedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task A_requester_sees_only_requester_visible_notes_while_staff_see_every_note()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		_ = await port.AddNoteAsync(AddNoteRequest(jobManagerId, submitted.JobNodeId, "Private triage note", false));
		_ = await port.AddNoteAsync(AddNoteRequest(jobManagerId, submitted.JobNodeId, "Public update", true));

		var requesterView = await port.GetDetailAsync(DetailRequest(requesterId, submitted.JobNodeId));
		var staffView = await port.GetDetailAsync(DetailRequest(jobManagerId, submitted.JobNodeId));

		requesterView.Notes.Should().ContainSingle().Which.Content.Should().Be("Public update");
		staffView.Notes.Should().HaveCount(2);
	}

	[Fact]
	public async Task A_different_requester_cannot_view_someone_elses_request_detail()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var otherRequesterId = await SeedEmployeeAsync("Ravi Requester", "ravi.requester", EmployeeRole.Requester);
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var port = CreateCommandPort(database.ConnectionString);
		var submitted = await port.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));

		var act = () => port.GetDetailAsync(DetailRequest(otherRequesterId, submitted.JobNodeId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Staff_browsing_the_holding_areas_own_node_sees_the_submitted_request_as_an_ordinary_child()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var holdingAreaId = await SeedHoldingAreaAsync(null, null, true);
		var requestPort = CreateCommandPort(database.ConnectionString);
		var submitted = await requestPort.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		var holdingAreaNodeId = await ReadHoldingAreaJobNodeIdAsync(holdingAreaId);
		var browsePort = CreateBrowsePort(database.ConnectionString);

		var children = await browsePort.GetChildrenAsync(holdingAreaNodeId, OwnershipFilter.All, JobArchiveFilter.All);

		var row = children.Should().ContainSingle(c => c.Id == submitted.JobNodeId).Which;
		row.Description.Should().Be(submitted.Description);
		row.OwnerUserId.Should().BeNull();
	}

	[Fact]
	public async Task Decomposing_a_requester_job_preserves_the_job_request_anchor()
	{
		var (_, requesterId) = await SeedRootAndRequesterAsync();
		var jobManagerId = await SeedEmployeeAsync("Priya Manager", "priya.manager", EmployeeRole.JobManager);
		var holdingAreaId = await SeedHoldingAreaAsync(
			null, jobManagerId, true);
		var requestPort = CreateCommandPort(database.ConnectionString);
		var submitted = await requestPort.SubmitAsync(SubmitRequest(requesterId, holdingAreaId));
		var nodeCommandPort = CreateJobNodeCommandPort(database.ConnectionString);
		var jobManagerContext = new CommandContext { Actor = jobManagerId, CorrelationId = Guid.NewGuid() };
		_ = await nodeCommandPort.AttachLeafWorkAsync(new() { Context = jobManagerContext, JobNodeId = submitted.JobNodeId });

		_ = await nodeCommandPort.DecomposeWorkedLeafAsync(new() {
			Context = jobManagerContext,
			LeafNodeId = submitted.JobNodeId,
			Version = submitted.Version,
			BranchDescription = "Printer troubleshooting",
			ExistingWorkDescription = "Diagnose the fault",
			NewChildren = [
				new() { Description = "Order replacement part", OwnerUserId = jobManagerId, Priority = Priority.Medium },
			],
		});

		var (anchoredHoldingAreaId, anchoredRequesterId) = await ReadJobRequestAnchorAsync(submitted.JobNodeId);
		anchoredHoldingAreaId.Should().Be(holdingAreaId);
		anchoredRequesterId.Should().Be(requesterId);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobRequestCommandPort CreateCommandPort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	/// <summary>
	///     The existing staff browse query — proves the holding-area queue view (plan §5, §9
	///     Stage 5) needs no new query, only calling it on the holding area's own <c>job_node_id</c>.
	/// </summary>
	internal abstract IJobBrowseQueryPort CreateBrowsePort(string connectionString);

	/// <summary>
	///     The existing job-node command port — used to prove decomposition preserves the
	///     <c>job_request</c> anchor (plan §5, §9 Stage 5), reusing the ordinary decompose command.
	/// </summary>
	internal abstract IJobNodeCommandPort CreateJobNodeCommandPort(string connectionString);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	private static SubmitJobRequestRequest SubmitRequest(AppUserId requesterId, RequestHoldingAreaId holdingAreaId) => new() {
		Context = new() { Actor = requesterId, CorrelationId = Guid.NewGuid() },
		HoldingAreaId = holdingAreaId,
		Description = "Printer will not turn on",
	};

	private static MoveRequesterJobRequest MoveRequest(AppUserId actorId, JobNodeId nodeId, JobNodeId newParentId, long version) => new() {
		Context = new() { Actor = actorId, CorrelationId = Guid.NewGuid() },
		NodeId = nodeId,
		NewParentId = newParentId,
		Version = version,
	};

	private static AcknowledgeJobRequestRequest AcknowledgeRequest(AppUserId actorId, JobNodeId nodeId, long version) => new() {
		Context = new() { Actor = actorId, CorrelationId = Guid.NewGuid() },
		NodeId = nodeId,
		Version = version,
	};

	private static AddJobRequestNoteRequest AddNoteRequest(AppUserId actorId, JobNodeId nodeId, string content, bool visibleToRequester) => new() {
		Context = new() { Actor = actorId, CorrelationId = Guid.NewGuid() },
		NodeId = nodeId,
		Content = content,
		VisibleToRequester = visibleToRequester,
	};

	private static GetJobRequestDetailRequest DetailRequest(AppUserId actorId, JobNodeId nodeId) => new() {
		Context = new() { Actor = actorId, CorrelationId = Guid.NewGuid() },
		NodeId = nodeId,
	};

	/// <summary>
	///     Deploys the schema, seeds a root and administrator via the real bootstrap port, and
	///     one <see cref="EmployeeRole.Requester" /> employee.
	/// </summary>
	private async Task<(JobNodeId RootId, AppUserId RequesterId)> SeedRootAndRequesterAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		var requesterId = await SeedEmployeeAsync("Rita Requester", "rita.requester", EmployeeRole.Requester);

		return (result.RootJobNodeId, requesterId);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string displayName, string userName, EmployeeRole role)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityUserCommand = connection.CreateCommand();
		identityUserCommand.CommandText = """
										  INSERT INTO identity_user
										    (app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										     concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										  VALUES
										    (@appUserId, @userName, @normalizedUserName, 'test-hash', @securityStamp,
										     @concurrencyStamp, @requiresPasswordChange, @isEnabled, @lockoutEnabled, 0);
										  """;
		AddParameter(identityUserCommand, "@appUserId", appUserId.Value);
		AddParameter(identityUserCommand, "@userName", userName);
		AddParameter(identityUserCommand, "@normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@requiresPasswordChange", false);
		AddParameter(identityUserCommand, "@isEnabled", true);
		AddParameter(identityUserCommand, "@lockoutEnabled", true);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await AssignRoleAsync(connection, appUserId, role);

		return appUserId;
	}

	private static async Task AssignRoleAsync(DbConnection connection, AppUserId appUserId, EmployeeRole role)
	{
		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();
	}

	private async Task<DepartmentId> SeedDepartmentAsync(string name)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO department (name, is_active)
							  VALUES (@name, true)
							  RETURNING id;
							  """;
		AddParameter(command, "@name", name);
		return new(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
	}

	private async Task SeedAppUserDepartmentAsync(AppUserId appUserId, DepartmentId departmentId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO app_user_department (app_user_id, department_id) VALUES (@appUserId, @departmentId);";
		AddParameter(command, "@appUserId", appUserId.Value);
		AddParameter(command, "@departmentId", departmentId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<RequestHoldingAreaId> SeedHoldingAreaAsync(
		DepartmentId? departmentId, AppUserId? defaultOwnerUserId, bool isActive)
	{
		await using var connection = await OpenExistingConnectionAsync();

		var rootId = await ReadRootIdAsync(connection);

		await using var nodeCommand = connection.CreateCommand();
		nodeCommand.CommandText = """
								  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								  VALUES (@parentId, 'Holding area', @postedByUserId, @postedByUserId, @priorityId, @postedAt)
								  RETURNING id;
								  """;
		AddParameter(nodeCommand, "@parentId", rootId);
		AddParameter(nodeCommand, "@postedByUserId", rootId);
		AddParameter(nodeCommand, "@priorityId", PriorityMedium);
		AddParameter(nodeCommand, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));
		var jobNodeId = Convert.ToInt64(await nodeCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		await using var holdingAreaCommand = connection.CreateCommand();
		holdingAreaCommand.CommandText = """
										 INSERT INTO request_holding_area
										    (job_node_id, department_id, name, default_priority_id, default_owner_user_id, is_active)
										 VALUES
										    (@jobNodeId, @departmentId, 'IT Intake', @priorityId, @defaultOwnerUserId, @isActive)
										 RETURNING id;
										 """;
		AddParameter(holdingAreaCommand, "@jobNodeId", jobNodeId);
		AddParameter(holdingAreaCommand, "@departmentId", (object?)departmentId?.Value ?? DBNull.Value);
		AddParameter(holdingAreaCommand, "@priorityId", PriorityMedium);
		AddParameter(holdingAreaCommand, "@defaultOwnerUserId", (object?)defaultOwnerUserId?.Value ?? DBNull.Value);
		AddParameter(holdingAreaCommand, "@isActive", isActive);

		return new(Convert.ToInt64(await holdingAreaCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
	}

	private static async Task<long> ReadRootIdAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT id FROM job_node WHERE parent_id IS NULL;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> InsertNodeAsync(long parentId, AppUserId ownerUserId, string description)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES (@parentId, @description, @ownerUserId, @ownerUserId, @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", parentId);
		AddParameter(command, "@description", description);
		AddParameter(command, "@ownerUserId", ownerUserId.Value);
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task SetHoldingAreaNodeOwnerAsync(RequestHoldingAreaId holdingAreaId, AppUserId ownerUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  UPDATE job_node SET owner_user_id = @ownerUserId
							  WHERE id = (SELECT job_node_id FROM request_holding_area WHERE id = @holdingAreaId);
							  """;
		AddParameter(command, "@ownerUserId", ownerUserId.Value);
		AddParameter(command, "@holdingAreaId", holdingAreaId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<JobNodeId> ReadHoldingAreaJobNodeIdAsync(RequestHoldingAreaId holdingAreaId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT job_node_id FROM request_holding_area WHERE id = @holdingAreaId;";
		AddParameter(command, "@holdingAreaId", holdingAreaId.Value);
		return new(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
	}

	private async Task<(RequestHoldingAreaId HoldingAreaId, AppUserId RequesterUserId)> ReadJobRequestAnchorAsync(JobNodeId jobNodeId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT holding_area_id, requester_user_id FROM job_request WHERE job_node_id = @jobNodeId;";
		AddParameter(command, "@jobNodeId", jobNodeId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		return (new(reader.GetInt64(0)), new(reader.GetInt64(1)));
	}

	private async Task SetActorAccountStateAsync(AppUserId appUserId, bool isEnabled, DateTimeOffset? lockoutEnd)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE identity_user SET is_enabled = @isEnabled, lockout_end = @lockoutEnd WHERE app_user_id = @appUserId;";
		AddParameter(command, "@isEnabled", isEnabled);
		AddParameter(command, "@lockoutEnd", lockoutEnd is null ? DBNull.Value : EncodeInstant(lockoutEnd.Value));
		AddParameter(command, "@appUserId", appUserId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
