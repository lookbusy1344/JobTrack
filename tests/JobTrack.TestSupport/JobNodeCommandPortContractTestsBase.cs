namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="IJobNodeCommandPort" />'s planning-node lifecycle methods (impl
///     plan §7.4 step 3, §7.3 slice 3: create, edit, move, archive, and conditionally delete),
///     asserted identically against PostgreSQL and SQLite by one thin sealed subclass per provider's
///     own test project -- same shape as <see cref="EmployeeQueryPortContractTestsBase" />. Mirrors
///     <c>JobCommandsTests</c>' scenarios against the fake port, so the real persistence
///     implementations are held to the same behavioural contract.
/// </summary>
public abstract class JobNodeCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private static readonly TimeSpan ActiveLockoutDuration = TimeSpan.FromHours(1);

	private readonly IDisposableTestDatabase database;

	protected JobNodeCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	/// <summary>
	///     Exposed so a provider-specific subclass can add its own concurrency/race tests
	///     (plan §6) that need to open additional ports/connections against the same database.
	/// </summary>
	protected string ConnectionString => database.ConnectionString;

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_job_manager_can_create_a_branch_and_a_leaf_under_the_root()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));

		branch.Kind.Should().Be(NodeKind.Leaf);
		branch.ParentId.Should().Be(rootId);
		branch.Version.Should().Be(1);
		leaf.Kind.Should().Be(NodeKind.Leaf);
	}

	[Fact]
	public async Task Creating_a_branch_writes_an_audit_event()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_node", EntityId = branch.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle();
		audit.Events[0].Operation.Should().Be("create-job-node");
		audit.Events[0].ActorId.Should().Be(jobManagerId);
	}

	[Fact]
	public async Task A_worker_cannot_create_a_node_under_a_root_they_do_not_own()
	{
		var (rootId, _, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.AddChildAsync(CreateRequest(workerId, workerId, rootId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_disabled_job_manager_cannot_create_a_node()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		await SetActorAccountStateAsync(jobManagerId, false, null);
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_locked_out_job_manager_cannot_create_a_node()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		await SetActorAccountStateAsync(jobManagerId, true, DateTimeOffset.UtcNow + ActiveLockoutDuration);
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_worker_can_create_a_node_under_a_branch_they_own()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var ownedBranch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var result = await port.AddChildAsync(CreateRequest(workerId, workerId, ownedBranch.Id));

		result.ParentId.Should().Be(ownedBranch.Id);
	}

	[Fact]
	public async Task Creating_a_node_under_a_nonexistent_parent_throws_not_found()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, new(rootId.Value + 999)));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Editing_a_node_replaces_its_editable_fields_and_bumps_the_version()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var result = await port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = branch.Id,
			Description = "Updated description",
			OwnerUserId = workerId,
			Priority = Priority.High,
			Version = branch.Version,
		});

		result.Description.Should().Be("Updated description");
		result.Priority.Should().Be(Priority.High);
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task Editing_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = branch.Id,
			Description = "Updated description",
			OwnerUserId = workerId,
			Priority = Priority.High,
			Version = branch.Version + 1,
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task A_controlling_owner_can_reassign_a_node_to_any_user()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.reassign", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var result = await port.EditAsync(new() {
			Context = ContextFor(workerId),
			NodeId = leaf.Id,
			Description = leaf.Description,
			OwnerUserId = otherWorkerId,
			Priority = leaf.Priority,
			Version = leaf.Version,
		});

		result.OwnerUserId.Should().Be(otherWorkerId);
	}

	[Fact]
	public async Task An_ancestor_owner_can_reassign_a_descendant_directly_owned_by_someone_else()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var branchOwnerId = await SeedEmployeeAsync("Branch Owner", "branch.owner.reassign", EmployeeRole.Worker);
		var descendantOwnerId = await SeedEmployeeAsync("Descendant Owner", "descendant.owner.reassign", EmployeeRole.Worker);
		var newOwnerId = await SeedEmployeeAsync("New Owner", "new.owner.reassign", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, branchOwnerId, rootId));
		var descendant = await port.AddChildAsync(CreateRequest(jobManagerId, descendantOwnerId, branch.Id));

		var result = await port.EditAsync(new() {
			Context = ContextFor(branchOwnerId),
			NodeId = descendant.Id,
			Description = descendant.Description,
			OwnerUserId = newOwnerId,
			Priority = descendant.Priority,
			Version = descendant.Version,
		});

		result.OwnerUserId.Should().Be(newOwnerId);
	}

	[Fact]
	public async Task Reassigning_a_node_writes_an_audit_event()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.reassign-audit", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		_ = await port.EditAsync(new() {
			Context = ContextFor(workerId),
			NodeId = leaf.Id,
			Description = leaf.Description,
			OwnerUserId = otherWorkerId,
			Priority = leaf.Priority,
			Version = leaf.Version,
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_node", EntityId = leaf.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle(e => e.Operation == "edit-job-node");
	}

	[Fact]
	public async Task A_controlling_owner_can_release_a_node_to_the_pool()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var result = await port.EditAsync(new() {
			Context = ContextFor(workerId),
			NodeId = leaf.Id,
			Description = leaf.Description,
			OwnerUserId = null,
			Priority = leaf.Priority,
			Version = leaf.Version,
		});

		result.OwnerUserId.Should().BeNull();
	}

	[Fact]
	public async Task Releasing_the_root_to_the_pool_is_rejected()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var root = await port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = rootId,
			Description = "Root",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
			Version = 1,
		});

		var act = () => port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = rootId,
			Description = root.Description,
			OwnerUserId = null,
			Priority = root.Priority,
			Version = root.Version,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-root-owner-required");
	}

	[Fact]
	public async Task A_non_controlling_worker_cannot_reassign_a_node()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.no-reassign", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.EditAsync(new() {
			Context = ContextFor(otherWorkerId),
			NodeId = leaf.Id,
			Description = leaf.Description,
			OwnerUserId = otherWorkerId,
			Priority = leaf.Priority,
			Version = leaf.Version,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Moving_a_node_updates_its_parent()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branchA = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		var branchB = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));

		var result = await port.MoveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = branchB.Id,
			NewParentId = branchA.Id,
			Version = branchB.Version,
		});

		result.ParentId.Should().Be(branchA.Id);
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task Moving_a_node_under_its_own_descendant_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var parent = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		var child = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, parent.Id));

		var act = () => port.MoveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = parent.Id,
			NewParentId = child.Id,
			Version = parent.Version,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-move-would-cycle");
	}

	[Fact]
	public async Task A_worker_cannot_move_a_node_into_a_subtree_they_do_not_own()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var ownedBranch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var otherBranch = await port.AddChildAsync(CreateRequest(jobManagerId, otherWorkerId, rootId));

		var act = () => port.MoveAsync(new() {
			Context = ContextFor(workerId),
			NodeId = ownedBranch.Id,
			NewParentId = otherBranch.Id,
			Version = ownedBranch.Version,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Moving_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branchA = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		var branchB = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));

		var act = () => port.MoveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = branchB.Id,
			NewParentId = branchA.Id,
			Version = branchB.Version + 1,
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Archiving_a_node_sets_archived_at_without_deleting_it()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var result = await port.ArchiveAsync(new() { Context = ContextFor(jobManagerId), NodeId = branch.Id, Version = branch.Version });

		result.ArchivedAt.Should().NotBeNull();
		result.Version.Should().Be(2);

		var stillEditable = await port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = branch.Id,
			Description = "Still here",
			OwnerUserId = workerId,
			Priority = Priority.Low,
			Version = result.Version,
		});
		stillEditable.ArchivedAt.Should().Be(result.ArchivedAt);
	}

	[Fact]
	public async Task Deleting_an_unused_node_removes_it()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		await port.DeleteAsync(new() { Context = ContextFor(jobManagerId), NodeId = branch.Id, Version = branch.Version });

		var act = () => port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = branch.Id,
			Description = "irrelevant",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Deleting_a_node_with_children_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var parent = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		_ = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, parent.Id));

		var act = () => port.DeleteAsync(new() { Context = ContextFor(jobManagerId), NodeId = parent.Id, Version = parent.Version });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-children-cannot-delete");
	}

	[Fact]
	public async Task Deleting_the_root_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.DeleteAsync(new() { Context = ContextFor(jobManagerId), NodeId = rootId, Version = 1 });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-is-root-cannot-delete");
	}

	[Fact]
	public async Task Deleting_a_node_with_a_prerequisite_edge_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		var dependent = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		await port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		var act = () => port.DeleteAsync(new() { Context = ContextFor(jobManagerId), NodeId = required.Id, Version = required.Version });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-prerequisites-cannot-delete");
	}

	[Fact]
	public async Task Deleting_a_leaf_with_unused_leaf_work_removes_it_and_its_leaf_work()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });

		await port.DeleteAsync(new() { Context = ContextFor(jobManagerId), NodeId = leaf.Id, Version = leaf.Version });

		var act = () => port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leaf.Id,
			Description = "irrelevant",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_non_administrator_cannot_delete_a_worked_leaf()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });
		_ = await SeedWorkSessionAsync(leaf.Id, workerId, DateTimeOffset.Parse("2026-01-01T09:00:00Z", CultureInfo.InvariantCulture),
			DateTimeOffset.Parse("2026-01-01T10:00:00Z", CultureInfo.InvariantCulture));

		var act = () => port.DeleteAsync(new() {
			Context = ContextFor(workerId),
			NodeId = leaf.Id,
			Version = leaf.Version,
			Reason = "Trying anyway.",
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_deleting_a_worked_leaf_without_a_reason_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var administratorId = await SeedEmployeeAsync("Ada Admin", "ada.admin.delete-no-reason", EmployeeRole.Administrator);
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });
		_ = await SeedWorkSessionAsync(leaf.Id, workerId, DateTimeOffset.Parse("2026-01-01T09:00:00Z", CultureInfo.InvariantCulture),
			DateTimeOffset.Parse("2026-01-01T10:00:00Z", CultureInfo.InvariantCulture));

		var act = () => port.DeleteAsync(new() { Context = ContextFor(administratorId), NodeId = leaf.Id, Version = leaf.Version });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-delete-worked-leaf-reason-required");
	}

	[Fact]
	public async Task An_administrator_can_delete_a_worked_leaf_with_a_reason()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var administratorId = await SeedEmployeeAsync("Ada Admin", "ada.admin.delete-with-reason", EmployeeRole.Administrator);
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });
		_ = await SeedWorkSessionAsync(leaf.Id, workerId, DateTimeOffset.Parse("2026-01-01T09:00:00Z", CultureInfo.InvariantCulture),
			DateTimeOffset.Parse("2026-01-01T10:00:00Z", CultureInfo.InvariantCulture));

		await port.DeleteAsync(new() {
			Context = ContextFor(administratorId),
			NodeId = leaf.Id,
			Version = leaf.Version,
			Reason = "Created and worked in error; duplicate of another job.",
		});

		var act = () => port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leaf.Id,
			Description = "irrelevant",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Deleting_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.DeleteAsync(new() { Context = ContextFor(jobManagerId), NodeId = branch.Id, Version = branch.Version + 1 });

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Attaching_leaf_work_to_a_bare_leaf_starts_at_waiting()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));

		var result = await port.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leaf.Id,
			FullCriteria = "Done when shipped",
		});

		result.Achievement.Should().Be(Achievement.Waiting);
		result.FullCriteria.Should().Be("Done when shipped");
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task Attaching_leaf_work_to_a_branch_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		_ = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, branch.Id));

		var act = () => port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = branch.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-children-cannot-attach-leaf-work");
	}

	[Fact]
	public async Task Attaching_leaf_work_to_the_root_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = rootId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-is-root-cannot-attach-leaf-work");
	}

	[Fact]
	public async Task Attaching_leaf_work_twice_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, jobManagerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });

		var act = () => port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("leaf-work-already-attached");
	}

	[Fact]
	public async Task A_worker_cannot_attach_leaf_work_to_a_leaf_they_do_not_own()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.attach", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, otherWorkerId, rootId));

		var act = () => port.AttachLeafWorkAsync(new() { Context = ContextFor(workerId), JobNodeId = leaf.Id });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Decomposing_a_worked_leaf_creates_the_expected_children_and_converts_it_to_a_branch()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id, FullCriteria = "Done when shipped" });
		var sessionId = await SeedWorkSessionAsync(leaf.Id, workerId, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
		var beforeDecompose = await ReadWorkSessionPreservedFieldsAsync(sessionId);

		var result = await port.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafNodeId = leaf.Id,
			Version = leaf.Version,
			BranchDescription = "Umbrella job",
			ExistingWorkDescription = "The work already done",
			NewChildren = [
				new() { Description = "New sub-job", OwnerUserId = workerId, Priority = Priority.Medium },
			],
		});

		result.NewChildIds.Should().HaveCount(1);
		result.BranchVersion.Should().Be(2);

		var branch = await port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = result.BranchId,
			Description = "Umbrella job",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			Version = result.BranchVersion,
		});
		branch.Kind.Should().Be(NodeKind.Branch);
		branch.ParentId.Should().Be(rootId);

		var existingWorkChild = await port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = result.ExistingWorkChildId,
			Description = "The work already done",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			// The reparent from its transient placement onto the branch (impl plan §7.3 step 4)
			// bumps this child's own version, same as any other job_node structural write.
			Version = 2,
		});
		existingWorkChild.Kind.Should().Be(NodeKind.Leaf);
		existingWorkChild.ParentId.Should().Be(result.BranchId);

		var (movedLeafWorkId, movedFullCriteria) = await ReadWorkSessionLeafWorkAsync(sessionId);
		movedLeafWorkId.Should().Be(result.ExistingWorkChildId);
		movedFullCriteria.Should().Be("Done when shipped");

		// Spec §4.5: decomposition preserves session identifiers, users, and times untouched --
		// only leaf_work_id is repointed (asserted separately above).
		var afterDecompose = await ReadWorkSessionPreservedFieldsAsync(sessionId);
		afterDecompose.Should().BeEquivalentTo(beforeDecompose);
	}

	[Fact]
	public async Task Concurrent_decomposes_of_the_same_leaf_allow_exactly_one_to_succeed()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var portA = CreateCommandPort(database.ConnectionString);
		var portB = CreateCommandPort(database.ConnectionString);
		var leaf = await portA.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await portA.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });

		var results = await Task.WhenAll(
			TryDecomposeAsync(portA, jobManagerId, leaf),
			TryDecomposeAsync(portB, jobManagerId, leaf));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	private static async Task<bool> TryDecomposeAsync(IJobNodeCommandPort port, AppUserId actor, JobNodeResult leaf)
	{
		try {
			_ = await port.DecomposeWorkedLeafAsync(new() {
				Context = ContextFor(actor),
				LeafNodeId = leaf.Id,
				Version = leaf.Version,
				BranchDescription = "Umbrella job",
				ExistingWorkDescription = "The work already done",
				NewChildren = [],
			});
			return true;
		}
		catch (JobTrackException) {
			// PostgreSQL allows genuine interleaving (MVCC), so the loser reads a version that is
			// stale by the time it writes -- ConcurrencyConflictException. SQLite's BEGIN IMMEDIATE
			// fully serializes the two attempts instead: the loser only starts once the winner has
			// already committed, so it finds no LeafWork left to decompose -- InvariantViolationException.
			// Both are the same underlying "did not win the race" outcome under each provider's own
			// concurrency model; this test asserts mutual exclusion, not a specific exception category.
			return false;
		}
	}

	[Fact]
	public async Task Decomposing_a_leaf_with_no_leaf_work_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafNodeId = leaf.Id,
			Version = leaf.Version,
			BranchDescription = "Umbrella job",
			ExistingWorkDescription = "The work already done",
			NewChildren = [],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("leaf-work-not-attached");
	}

	[Fact]
	public async Task Decomposing_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });

		var act = () => port.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafNodeId = leaf.Id,
			Version = leaf.Version + 1,
			BranchDescription = "Umbrella job",
			ExistingWorkDescription = "The work already done",
			NewChildren = [],
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Importing_a_subtree_creates_every_node_and_prerequisite_edge_in_one_transaction()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var result = await port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1,
					ParentLocalId = null,
					Description = "Branch",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
				},
				new() {
					LocalId = 2,
					ParentLocalId = 1,
					Description = "Child A",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
				},
				new() {
					LocalId = 3,
					ParentLocalId = 1,
					Description = "Child B",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					PrerequisiteLocalIds = [2],
				},
			],
		});

		result.Nodes.Should().HaveCount(3);
		var branchId = result.Nodes.Single(n => n.LocalId == 1).JobNodeId;
		var childAId = result.Nodes.Single(n => n.LocalId == 2).JobNodeId;
		var childBId = result.Nodes.Single(n => n.LocalId == 3).JobNodeId;

		var branch = await port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = branchId,
			Description = "Branch",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		branch.Kind.Should().Be(NodeKind.Branch);
		branch.ParentId.Should().Be(rootId);

		var act = () => port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = childAId, DependentJobId = childBId });
		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-already-exists");
	}

	[Fact]
	public async Task Importing_a_subtree_writes_a_single_audit_event()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		_ = await port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() { LocalId = 1, Description = "Solo node", OwnerUserId = workerId, Priority = Priority.Medium },
			],
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_node", EntityId = rootId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle(e => e.Operation == "import-subtree");
	}

	[Fact]
	public async Task A_worker_cannot_import_a_subtree_under_a_root_they_do_not_own()
	{
		var (rootId, _, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(workerId),
			ParentId = rootId,
			Nodes = [
				new() { LocalId = 1, Description = "Solo node", OwnerUserId = workerId, Priority = Priority.Medium },
			],
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Importing_a_subtree_with_an_invalid_prerequisite_edge_creates_nothing()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var childrenBefore = await CountChildrenAsync(rootId);

		// A child cannot require its own parent -- ancestor/descendant edges are rejected (spec §6
		// rule 4) -- so this fails on the second node's prerequisite, after the first node (the
		// parent) has already been flushed to the open transaction. Asserting the parent does not
		// survive proves the whole batch rolled back, not just the edge.
		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1,
					ParentLocalId = null,
					Description = "Parent",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
				},
				new() {
					LocalId = 2,
					ParentLocalId = 1,
					Description = "Child",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					PrerequisiteLocalIds = [1],
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-is-hierarchy-edge");

		var childrenAfter = await CountChildrenAsync(rootId);
		childrenAfter.Should().Be(childrenBefore);
	}

	[Fact]
	public async Task Importing_a_subtree_records_open_and_closed_work_on_its_leaves()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var now = SystemClock.Instance.GetCurrentInstant();

		var result = await port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1,
					Description = "Closed leaf",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = workerId,
						StartedAt = now - Duration.FromDays(3),
						FinishedAt = now - Duration.FromDays(2),
						Achievement = Achievement.Success,
					},
				},
				new() {
					LocalId = 2,
					Description = "Open leaf",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					PrerequisiteLocalIds = [1],
					LeafWork = new() {
						WorkedByUserId = workerId, StartedAt = now - Duration.FromDays(1), FinishedAt = null, Achievement = Achievement.InProgress,
					},
				},
			],
		});

		var closedId = result.Nodes.Single(n => n.LocalId == 1).JobNodeId;
		var openId = result.Nodes.Single(n => n.LocalId == 2).JobNodeId;

		(await ReadAchievementIdAsync(closedId)).Should().Be((long)Achievement.Success);
		(await ReadAchievementIdAsync(openId)).Should().Be((long)Achievement.InProgress);

		(await CountSessionsAsync(closedId, true)).Should().Be(1);
		(await CountSessionsAsync(closedId, false)).Should().Be(0);
		(await CountSessionsAsync(openId, true)).Should().Be(0);
		(await CountSessionsAsync(openId, false)).Should().Be(1);
	}

	[Fact]
	public async Task Importing_worked_leaves_under_a_blocked_parent_creates_nothing()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var now = SystemClock.Instance.GetCurrentInstant();

		// The gate the batch itself cannot see: the anchor node inherits a prerequisite on a
		// pre-existing node that has never succeeded, so no work may be recorded beneath it. Only the
		// providers' in-transaction readiness recheck catches this -- SubtreeImportPlanner reasons
		// about the batch's own edges alone.
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var anchor = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		await port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = required.Id, DependentJobId = anchor.Id });

		var childrenBefore = await CountChildrenAsync(anchor.Id);

		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = anchor.Id,
			Nodes = [
				new() {
					LocalId = 1,
					Description = "Blocked leaf",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = workerId,
						StartedAt = now - Duration.FromHours(2),
						FinishedAt = now - Duration.FromHours(1),
						Achievement = Achievement.Success,
					},
				},
			],
		});

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();

		(await CountChildrenAsync(anchor.Id)).Should().Be(childrenBefore);
	}

	[Fact]
	public async Task Importing_a_subtree_whose_work_starts_in_the_future_creates_nothing()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var now = SystemClock.Instance.GetCurrentInstant();
		var childrenBefore = await CountChildrenAsync(rootId);

		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1,
					Description = "Leaf worked tomorrow",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = workerId,
						StartedAt = now + Duration.FromDays(1),
						FinishedAt = now + Duration.FromDays(2),
						Achievement = Achievement.Success,
					},
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-start-in-future");

		(await CountChildrenAsync(rootId)).Should().Be(childrenBefore);
	}

	private async Task<long> ReadAchievementIdAsync(JobNodeId leafId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT achievement_id FROM leaf_work WHERE job_node_id = @leafId;";
		AddParameter(command, "@leafId", leafId.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	/// <summary>
	///     Counts <paramref name="leafId" />'s sessions that have finished (or, when
	///     <paramref name="finished" /> is <see langword="false" />, that are still open). Asserting on
	///     the null-ness of <c>finished_at</c> rather than its value keeps the contract provider-neutral:
	///     SQLite stores instants as integers and PostgreSQL as <c>timestamptz</c>.
	/// </summary>
	private async Task<long> CountSessionsAsync(JobNodeId leafId, bool finished)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"SELECT COUNT(*) FROM work_session WHERE leaf_work_id = @leafId AND finished_at IS "
			+ (finished ? "NOT NULL" : "NULL") + ";";
		AddParameter(command, "@leafId", leafId.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> CountChildrenAsync(JobNodeId parentId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM job_node WHERE parent_id = @parentId;";
		AddParameter(command, "@parentId", parentId.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	[Fact]
	public async Task Adding_a_prerequisite_between_unrelated_leaves_succeeds()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var dependent = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		await port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-already-exists");
	}

	[Fact]
	public async Task A_job_cannot_require_itself()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = leaf.Id, DependentJobId = leaf.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-not-self");
	}

	[Fact]
	public async Task A_prerequisite_edge_between_ancestor_and_descendant_is_rejected()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var parent = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var child = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, parent.Id));

		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = parent.Id,
			DependentJobId = child.Id,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-is-hierarchy-edge");
	}

	[Fact]
	public async Task A_prerequisite_edge_between_descendant_and_ancestor_is_rejected()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var parent = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var child = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, parent.Id));

		// The reverse direction of A_prerequisite_edge_between_ancestor_and_descendant_is_rejected:
		// ValidatePrerequisiteEdgeAsync checks both "required is an ancestor of dependent" and
		// "dependent is an ancestor of required" independently, so both directions need coverage.
		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = child.Id,
			DependentJobId = parent.Id,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-is-hierarchy-edge");
	}

	[Fact]
	public async Task A_prerequisite_edge_that_would_create_a_cycle_is_rejected()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var a = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var b = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		await port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = a.Id, DependentJobId = b.Id });

		var act = () => port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = b.Id, DependentJobId = a.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-would-cycle");
	}

	[Fact]
	public async Task Removing_a_prerequisite_allows_it_to_be_added_again()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var dependent = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		await port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		await port.RemovePrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		await port.AddPrerequisiteAsync(new() { Context = ContextFor(jobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });
	}

	[Fact]
	public async Task Removing_a_nonexistent_prerequisite_throws_not_found()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var dependent = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.RemovePrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_worker_cannot_add_a_prerequisite_involving_a_job_they_do_not_own()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.prereq", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var dependent = await port.AddChildAsync(CreateRequest(jobManagerId, otherWorkerId, rootId));

		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(workerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Picking_up_an_unassigned_node_sets_the_actor_as_owner()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var unassigned = await port.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});

		var result = await port.PickUpAsync(new() { Context = ContextFor(workerId), NodeId = unassigned.Id });

		result.OwnerUserId.Should().Be(workerId);
	}

	[Fact]
	public async Task Picking_up_a_node_writes_an_audit_event()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var unassigned = await port.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});

		_ = await port.PickUpAsync(new() { Context = ContextFor(workerId), NodeId = unassigned.Id });

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "job_node", EntityId = unassigned.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle(e => e.Operation == "pick-up-job-node");
		audit.Events[0].ActorId.Should().Be(workerId);
	}

	[Fact]
	public async Task Picking_up_an_already_owned_node_throws_already_claimed_regardless_of_role()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.pickup", EmployeeRole.Worker);
		var port = CreateCommandPort(database.ConnectionString);
		var owned = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var workerAttempt = () => port.PickUpAsync(new() { Context = ContextFor(otherWorkerId), NodeId = owned.Id });
		var jobManagerAttempt = () => port.PickUpAsync(new() { Context = ContextFor(jobManagerId), NodeId = owned.Id });

		(await workerAttempt.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-already-claimed");
		(await jobManagerAttempt.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-already-claimed");
	}

	[Fact]
	public async Task Picking_up_an_unassigned_branch_grants_control_over_a_descendant_leaf()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var unassignedBranch = await port.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool branch",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		var descendantLeaf = await port.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = unassignedBranch.Id,
			Description = "Descendant leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});

		_ = await port.PickUpAsync(new() { Context = ContextFor(workerId), NodeId = unassignedBranch.Id });

		var result = await port.EditAsync(new() {
			Context = ContextFor(workerId),
			NodeId = descendantLeaf.Id,
			Description = "Renamed by controlling owner",
			OwnerUserId = null,
			Priority = Priority.Medium,
			Version = descendantLeaf.Version,
		});

		result.Description.Should().Be("Renamed by controlling owner");
	}

	[Fact]
	public async Task A_read_only_role_cannot_pick_up_an_unassigned_node()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var auditorId = await SeedEmployeeAsync("Auditor Alice", "auditor.alice.pickup", EmployeeRole.Auditor);
		var port = CreateCommandPort(database.ConnectionString);
		var unassigned = await port.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});

		var act = () => port.PickUpAsync(new() { Context = ContextFor(auditorId), NodeId = unassigned.Id });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Picking_up_a_node_does_not_require_readiness()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var unassignedDependent = await port.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned blocked leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		await port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = unassignedDependent.Id,
		});

		var result = await port.PickUpAsync(new() { Context = ContextFor(workerId), NodeId = unassignedDependent.Id });

		result.OwnerUserId.Should().Be(workerId);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateCommandPort(string connectionString);

	protected abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static CreateJobNodeRequest CreateRequest(AppUserId actor, AppUserId owner, JobNodeId parentId) => new() {
		Context = ContextFor(actor),
		ParentId = parentId,
		Description = "Do the thing",
		OwnerUserId = owner,
		Priority = Priority.Medium,
	};

	/// <summary>
	///     Seeds a deployed schema, an administrator/root via the real bootstrap port (with the
	///     administrator additionally granted <see cref="EmployeeRole.JobManager" />, since bootstrap
	///     itself assigns no roles), and one <see cref="EmployeeRole.Worker" /> employee. Exposed (rather
	///     than private) so a provider-specific subclass can add its own concurrency/race tests (plan
	///     §6) reusing the same root/user seeding.
	/// </summary>
	protected async Task<(JobNodeId RootId, AppUserId JobManagerId, AppUserId WorkerId)> SeedRootAndUsersAsync()
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

		await using (var connection = await OpenExistingConnectionAsync()) {
			await AssignRoleAsync(connection, result.AdministratorId, EmployeeRole.JobManager);
		}

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper", EmployeeRole.Worker);

		return (result.RootJobNodeId, result.AdministratorId, workerId);
	}

	/// <summary>
	///     Exposed (rather than private) so a provider-specific subclass can seed an extra
	///     employee for its own concurrency/race tests (plan §6), the same reason
	///     <see cref="SeedRootAndUsersAsync" /> is exposed.
	/// </summary>
	protected async Task<AppUserId> SeedEmployeeAsync(string displayName, string userName, EmployeeRole role)
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

	private async Task<WorkSessionId> SeedWorkSessionAsync(
		JobNodeId leafNodeId, AppUserId workedByUserId, DateTimeOffset startedAt, DateTimeOffset finishedAt)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  VALUES (@leafWorkId, @workedByUserId, @startedAt, @finishedAt, @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@leafWorkId", leafNodeId.Value);
		AddParameter(command, "@workedByUserId", workedByUserId.Value);
		AddParameter(command, "@startedAt", EncodeInstant(startedAt));
		AddParameter(command, "@finishedAt", EncodeInstant(finishedAt));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return new(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
	}

	/// <summary>
	///     Reads the columns spec §4.5 requires a decomposition to preserve untouched
	///     (identifiers, users, times), as raw provider values -- comparing these before/after a
	///     decompose proves preservation directly, without needing to decode each provider's own
	///     instant encoding back to a comparable .NET type.
	/// </summary>
	private async Task<(long WorkedByUserId, object StartedAt, object? FinishedAt)> ReadWorkSessionPreservedFieldsAsync(
		WorkSessionId sessionId)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT worked_by_user_id, started_at, finished_at FROM work_session WHERE id = @sessionId;";
		AddParameter(command, "@sessionId", sessionId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		return (reader.GetInt64(0), reader.GetValue(1), reader.IsDBNull(2) ? null : reader.GetValue(2));
	}

	private async Task<(JobNodeId LeafWorkId, string? FullCriteria)> ReadWorkSessionLeafWorkAsync(WorkSessionId sessionId)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT lw.job_node_id, lw.full_criteria
							  FROM work_session ws
							  JOIN leaf_work lw ON lw.job_node_id = ws.leaf_work_id
							  WHERE ws.id = @sessionId;
							  """;
		AddParameter(command, "@sessionId", sessionId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		var leafWorkId = new JobNodeId(reader.GetInt64(0));
		var fullCriteria = reader.IsDBNull(1) ? null : reader.GetString(1);
		return (leafWorkId, fullCriteria);
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
