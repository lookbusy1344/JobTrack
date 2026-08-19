namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;

public abstract partial class JobNodeCommandPortContractTestsBase
{
	[Fact]
	public async Task Decomposing_a_bare_leaf_creates_the_listed_children_and_converts_it_into_a_branch()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var result = await port.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafNodeId = leaf.Id,
			Version = leaf.Version,
			BranchDescription = "Umbrella job",
			NewChildren = [
				new() {
					Description = "First named child", OwnerUserId = workerId, Priority = Priority.Medium,
				},
				new() {
					Description = "Second named child", OwnerUserId = workerId, Priority = Priority.Medium,
				},
			],
		});

		result.ExistingWorkChildId.Should().BeNull();
		result.NewChildIds.Should().HaveCount(2);
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

		var firstChild = await port.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = result.NewChildIds[0],
			Description = "First named child",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		firstChild.ParentId.Should().Be(result.BranchId);
	}

	[Fact]
	public async Task Decomposing_a_bare_leaf_with_no_new_children_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafNodeId = leaf.Id,
			Version = leaf.Version,
			BranchDescription = "Umbrella job",
			NewChildren = [],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-decompose-requires-a-child");
	}

	[Fact]
	public async Task Decomposing_a_node_with_children_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var branch = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, branch.Id));

		var act = () => port.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafNodeId = branch.Id,
			Version = branch.Version,
			BranchDescription = "Umbrella job",
			NewChildren = [
				new() {
					Description = "Another child", OwnerUserId = workerId, Priority = Priority.Medium,
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-children-cannot-decompose");
	}

	[Fact]
	public async Task Decomposing_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		_ = await port.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leaf.Id,
		});

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

		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = childAId,
			DependentJobId = childBId,
		});
		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-already-exists");
	}

	[Fact]
	public async Task A_requester_cannot_be_assigned_as_the_owner_of_an_imported_node()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var requesterId = await SeedEmployeeAsync("Requesting User", "requesting.user.import-owner", EmployeeRole.Requester);
		var port = CreateCommandPort(database.ConnectionString);

		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1, Description = "Imported node", OwnerUserId = requesterId, Priority = Priority.Medium,
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-owner-not-eligible");
	}

	[Fact]
	public async Task A_requester_cannot_be_recorded_as_the_worker_during_subtree_import()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var requesterId = await SeedEmployeeAsync("Requesting User", "requesting.user.import-work", EmployeeRole.Requester);
		var port = CreateCommandPort(database.ConnectionString);
		var now = SystemClock.Instance.GetCurrentInstant();

		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1,
					Description = "Imported worked leaf",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = requesterId, StartedAt = now - Duration.FromHours(1), FinishedAt = now, Achievement = Achievement.Success,
					},
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-target-not-eligible");
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
				new() {
					LocalId = 1, Description = "Solo node", OwnerUserId = workerId, Priority = Priority.Medium,
				},
			],
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "job_node",
				EntityId = rootId.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

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
				new() {
					LocalId = 1, Description = "Solo node", OwnerUserId = workerId, Priority = Priority.Medium,
				},
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
						WorkedByUserId = workerId, StartedAt = now - Duration.FromDays(3), FinishedAt = now - Duration.FromDays(2), Achievement = Achievement.Success,
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

	[Theory]
	[InlineData(Achievement.Cancelled)]
	[InlineData(Achievement.Unsuccessful)]
	public async Task Importing_a_closed_leaf_preserves_multiple_finished_sessions(Achievement achievement)
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
					Description = "Closed leaf with history",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = workerId,
						StartedAt = now - Duration.FromDays(4),
						FinishedAt = now - Duration.FromDays(3),
						Achievement = achievement,
						AdditionalSessions = [
							new() {
								WorkedByUserId = workerId, StartedAt = now - Duration.FromDays(2), FinishedAt = now - Duration.FromDays(1),
							},
						],
					},
				},
			],
		});

		var leafId = result.Nodes.Single().JobNodeId;
		(await ReadAchievementIdAsync(leafId)).Should().Be((long)achievement);
		(await CountSessionsAsync(leafId, true)).Should().Be(2);
		(await CountSessionsAsync(leafId, false)).Should().Be(0);
	}

	[Fact]
	public async Task Importing_multiple_sessions_rolls_back_the_batch_when_an_additional_session_is_invalid()
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
					Description = "Invalid historical leaf",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = workerId,
						StartedAt = now - Duration.FromDays(2),
						FinishedAt = now - Duration.FromDays(1),
						Achievement = Achievement.Success,
						AdditionalSessions = [
							new() {
								WorkedByUserId = workerId, StartedAt = now + Duration.FromDays(1), FinishedAt = null,
							},
						],
					},
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-start-in-future");
		(await CountChildrenAsync(rootId)).Should().Be(childrenBefore);
	}

	[Fact]
	public async Task Archiving_a_leaf_with_an_active_session_is_rejected()
	{
		// ADR 0044: import a leaf carrying an already-open (FinishedAt null) session, then attempt to
		// archive its node -- exercising the same closure invariant through the public command surface
		// rather than a raw-SQL bypass.
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var now = SystemClock.Instance.GetCurrentInstant();

		var imported = await port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1,
					Description = "Actively worked leaf",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = workerId, StartedAt = now - Duration.FromHours(1), FinishedAt = null, Achievement = Achievement.InProgress,
					},
				},
			],
		});
		var leafId = imported.Nodes.Single().JobNodeId;
		var version = await ReadNodeVersionAsync(leafId);

		var act = () => port.ArchiveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leafId,
			Version = version,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("leaf-closure-active-sessions");
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
		await port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = anchor.Id,
		});

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
						WorkedByUserId = workerId, StartedAt = now - Duration.FromHours(2), FinishedAt = now - Duration.FromHours(1), Achievement = Achievement.Success,
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
						WorkedByUserId = workerId, StartedAt = now + Duration.FromDays(1), FinishedAt = now + Duration.FromDays(2), Achievement = Achievement.Success,
					},
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-start-in-future");

		(await CountChildrenAsync(rootId)).Should().Be(childrenBefore);
	}

	/// <summary>
	///     Remediation plan §3.3: the import's home-node preferences are part of the import, resolved
	///     from its own local-id map inside the same transaction, under one actor and one correlation id
	///     — not a post-commit loop of per-account <c>SetHomeNodeAsync</c> calls.
	/// </summary>
	[Fact]
	public async Task Importing_a_subtree_sets_the_flagged_home_node_for_every_named_account()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var commandContext = ContextFor(jobManagerId);

		var result = await port.ImportSubtreeAsync(new() {
			Context = commandContext,
			ParentId = rootId,
			Nodes = HomeNodeImportBatch(workerId),
			HomeNodeLocalId = 1,
			HomeNodeUserIds = [jobManagerId, workerId],
		});

		var homeNodeId = result.Nodes.Single(n => n.LocalId == 1).JobNodeId;
		(await ReadHomeNodeIdAsync(jobManagerId)).Should().Be(homeNodeId.Value);
		(await ReadHomeNodeIdAsync(workerId)).Should().Be(homeNodeId.Value);

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				CorrelationId = commandContext.CorrelationId,
			}, null, AuditSearchTestDefaults.AllRowsLimit);
		audit.Events.Count(e => e.Operation == "set-home-node").Should().Be(2);
		audit.Events.Should().OnlyContain(e => e.ActorId == jobManagerId);
	}

	[Fact]
	public async Task A_leaf_home_node_rolls_the_whole_import_back()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var childrenBefore = await CountChildrenAsync(rootId);

		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = HomeNodeImportBatch(workerId),
			// Local id 2 is a childless node, so it imports as a leaf and cannot be a home node.
			HomeNodeLocalId = 2,
			HomeNodeUserIds = [jobManagerId],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("home-node-must-not-be-leaf");

		(await CountChildrenAsync(rootId)).Should().Be(childrenBefore);
		(await ReadHomeNodeIdAsync(jobManagerId)).Should().BeNull();
	}

	/// <summary>
	///     The plan's injected-failure case: the last named account does not exist. Nothing may commit —
	///     not the tree, not the earlier account's home node — because partial success is no longer a
	///     valid outcome of this command.
	/// </summary>
	[Fact]
	public async Task An_unknown_home_node_account_rolls_the_whole_import_back()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var childrenBefore = await CountChildrenAsync(rootId);

		var act = () => port.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = HomeNodeImportBatch(workerId),
			HomeNodeLocalId = 1,
			HomeNodeUserIds = [jobManagerId, new(NonExistentUserId)],
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();

		(await CountChildrenAsync(rootId)).Should().Be(childrenBefore);
		(await ReadHomeNodeIdAsync(jobManagerId)).Should().BeNull();
	}

	/// <summary>
	///     Remediation plan §3.3 step 1's target-account-state contention proof. A concurrent disable
	///     holds the shared employee-account lock row until it commits; the import must then re-read the authoritative state
	///     under its own transaction and reject every write rather than assigning a preference to an
	///     account that can no longer act.
	/// </summary>
	[Fact]
	public async Task A_target_disabled_while_home_node_import_waits_rolls_the_whole_import_back()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var childrenBefore = await CountChildrenAsync(rootId);

		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var transaction = await connection.BeginTransactionAsync();
		await using (var command = connection.CreateCommand()) {
			command.Transaction = transaction;
			command.CommandText = "UPDATE app_user SET row_version = row_version WHERE id = @appUserId; " +
								  "UPDATE identity_user SET is_enabled = @isEnabled WHERE app_user_id = @appUserId;";
			command.AddParameter("@isEnabled", false);
			command.AddParameter("@appUserId", workerId.Value);
			_ = await command.ExecuteNonQueryAsync();
		}

		var act = async () =>
			_ = await port.ImportSubtreeAsync(new() {
				Context = ContextFor(jobManagerId),
				ParentId = rootId,
				Nodes = HomeNodeImportBatch(jobManagerId),
				HomeNodeLocalId = 1,
				HomeNodeUserIds = [workerId],
			});
		var assertion = Task.Run(() => act.Should().ThrowAsync<InvariantViolationException>());

		try {
			var observation = Task.Delay(ContentionObservationTimeout);
			(await Task.WhenAny(assertion, observation)).Should().BeSameAs(observation,
				"the import should wait for the concurrent target-account transition");
		}
		finally {
			await transaction.CommitAsync();
		}

		(await assertion).Which.ConstraintId.Should().Be("home-node-target-not-active");
		(await CountChildrenAsync(rootId)).Should().Be(childrenBefore);
		(await ReadHomeNodeIdAsync(workerId)).Should().BeNull();
	}

	/// <summary>
	///     Remediation plan §3.3 step 1's contention case: two imports contend for the same target
	///     account's home node. Whichever order they land in, the account's <c>home_node_id</c> must name
	///     a node one of them actually committed — never a node from a rolled-back import, and never the
	///     null it started at.
	/// </summary>
	protected async Task AssertConcurrentHomeNodeImportsLeaveOneCommittedHomeNodeAsync()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();

		var results = await Task.WhenAll(
			ImportWithHomeNodeAsync(rootId, jobManagerId, workerId),
			ImportWithHomeNodeAsync(rootId, jobManagerId, workerId));

		var committedHomeNodeIds = results.Select(r => r.Nodes.Single(n => n.LocalId == 1).JobNodeId.Value).ToArray();
		var homeNodeId = await ReadHomeNodeIdAsync(workerId);
		homeNodeId.Should().NotBeNull();
		committedHomeNodeIds.Should().Contain(homeNodeId!.Value);
	}

	private Task<ImportSubtreeResult> ImportWithHomeNodeAsync(JobNodeId rootId, AppUserId actorId, AppUserId targetId) =>
		CreateCommandPort(database.ConnectionString).ImportSubtreeAsync(new() {
			Context = ContextFor(actorId),
			ParentId = rootId,
			Nodes = HomeNodeImportBatch(targetId),
			HomeNodeLocalId = 1,
			HomeNodeUserIds = [targetId],
		});

	/// <summary>A two-node batch whose local id 1 is a branch (a valid home node) and local id 2 a leaf.</summary>
	private static EquatableArray<ImportSubtreeNodeSpec> HomeNodeImportBatch(AppUserId ownerId) => [
		new() {
			LocalId = 1,
			ParentLocalId = null,
			Description = "Home branch",
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		},
		new() {
			LocalId = 2,
			ParentLocalId = 1,
			Description = "Child leaf",
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		},
	];

	private async Task<long?> ReadHomeNodeIdAsync(AppUserId userId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT home_node_id FROM app_user WHERE id = @userId;";
		command.AddParameter("@userId", userId.Value);
		var value = await command.ExecuteScalarAsync();
		return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
	}

	private async Task<long> ReadAchievementIdAsync(JobNodeId leafId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT achievement_id FROM leaf_work WHERE job_node_id = @leafId;";
		command.AddParameter("@leafId", leafId.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	/// <summary>The single still-open session on <paramref name="leafId" />; fails the test if there isn't exactly one.</summary>
	private async Task<WorkSessionId> ReadActiveSessionIdAsync(JobNodeId leafId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT id FROM work_session WHERE leaf_work_id = @leafId AND finished_at IS NULL;";
		command.AddParameter("@leafId", leafId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue("the leaf should have an open session");
		var sessionId = new WorkSessionId(reader.GetInt64(0));
		(await reader.ReadAsync()).Should().BeFalse("the leaf should have exactly one open session");

		return sessionId;
	}

	private async Task<long> CountLeafWorkAsync(JobNodeId nodeId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM leaf_work WHERE job_node_id = @nodeId;";
		command.AddParameter("@nodeId", nodeId.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long?> ReadOwnerUserIdAsync(JobNodeId nodeId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT owner_user_id FROM job_node WHERE id = @nodeId;";
		command.AddParameter("@nodeId", nodeId.Value);
		var value = await command.ExecuteScalarAsync();
		return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
	}

	private async Task<long> ReadNodeVersionAsync(JobNodeId nodeId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT row_version FROM job_node WHERE id = @nodeId;";
		command.AddParameter("@nodeId", nodeId.Value);
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
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText =
			"SELECT COUNT(*) FROM work_session WHERE leaf_work_id = @leafId AND finished_at IS "
			+ (finished ? "NOT NULL" : "NULL") + ";";
		command.AddParameter("@leafId", leafId.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> CountChildrenAsync(JobNodeId parentId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM job_node WHERE parent_id = @parentId;";
		command.AddParameter("@parentId", parentId.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> CountPrerequisitesAsync()
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM job_prerequisite;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	[Fact]
	public async Task Adding_a_prerequisite_between_unrelated_leaves_succeeds()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var dependent = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		await port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-already-exists");
	}

	[Fact]
	public async Task Adding_a_prerequisite_batch_rolls_back_every_edge_when_one_is_invalid()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var required = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));
		var dependent = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.AddPrerequisitesAsync(new() {
			Context = ContextFor(jobManagerId),
			Edges = [
				new(required.Id, dependent.Id),
				new(dependent.Id, dependent.Id),
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-not-self");
		(await CountPrerequisitesAsync()).Should().Be(0);
	}

	[Fact]
	public async Task A_job_cannot_require_itself()
	{
		var (rootId, jobManagerId, workerId) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(database.ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, workerId, rootId));

		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = leaf.Id,
			DependentJobId = leaf.Id,
		});

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
		await port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = a.Id,
			DependentJobId = b.Id,
		});

		var act = () => port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = b.Id,
			DependentJobId = a.Id,
		});

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
		await port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		await port.RemovePrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		await port.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
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

		var result = await port.PickUpAsync(new() {
			Context = ContextFor(workerId),
			NodeId = unassigned.Id,
		});

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

		_ = await port.PickUpAsync(new() {
			Context = ContextFor(workerId),
			NodeId = unassigned.Id,
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "job_node",
				EntityId = unassigned.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

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

		var workerAttempt = () => port.PickUpAsync(new() {
			Context = ContextFor(otherWorkerId),
			NodeId = owned.Id,
		});
		var jobManagerAttempt = () => port.PickUpAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = owned.Id,
		});

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

		_ = await port.PickUpAsync(new() {
			Context = ContextFor(workerId),
			NodeId = unassignedBranch.Id,
		});

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

		var act = () => port.PickUpAsync(new() {
			Context = ContextFor(auditorId),
			NodeId = unassigned.Id,
		});

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

		var result = await port.PickUpAsync(new() {
			Context = ContextFor(workerId),
			NodeId = unassignedDependent.Id,
		});

		result.OwnerUserId.Should().Be(workerId);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateCommandPort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	private static CreateJobNodeRequest CreateRequest(AppUserId actor, AppUserId? owner, JobNodeId parentId) => new() {
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
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);

			// PostgreSqlJobNodeCommandPort's worked-leaf/subtree deletion paths (ADR 0036/0061) call
			// the command-shaped SECURITY DEFINER functions from the unversioned functions
			// script rather than a schema-versions script -- so it must exist here too, even though
			// these tests connect as an administrator and never exercise the EXECUTE-grant boundary
			// PostgreSqlRoleGrantsTests covers. The roles script must apply first: the functions
			// script's GRANT EXECUTE targets (jobtrack_domain and friends) do not exist otherwise.
			if (Provider == SchemaProvider.PostgreSql) {
				await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlRolesAndGrantsScriptPath(), CancellationToken.None);
				await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlFunctionsScriptPath(), CancellationToken.None);
			}
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			await DatabaseContractTestSupport.AssignRoleAsync(connection, result.AdministratorId, EmployeeRole.JobManager);
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
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		appUserCommand.AddParameter("@displayName", displayName);
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
		identityUserCommand.AddParameter("@appUserId", appUserId.Value);
		identityUserCommand.AddParameter("@userName", userName);
		identityUserCommand.AddParameter("@normalizedUserName", userName.ToUpperInvariant());
		identityUserCommand.AddParameter("@securityStamp", Guid.NewGuid().ToString("N"));
		identityUserCommand.AddParameter("@concurrencyStamp", Guid.NewGuid().ToString("N"));
		identityUserCommand.AddParameter("@requiresPasswordChange", false);
		identityUserCommand.AddParameter("@isEnabled", true);
		identityUserCommand.AddParameter("@lockoutEnabled", true);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await DatabaseContractTestSupport.AssignRoleAsync(connection, appUserId, role);

		return appUserId;
	}

	/// <summary>
	///     Seeds a <c>request_holding_area</c> anchored at <paramref name="jobNodeId" />, written
	///     directly rather than through the intake ports: these delete tests need only the dependent
	///     row's existence, not the intake behaviour those ports' own contract tests already cover.
	/// </summary>
	private async Task<long> SeedHoldingAreaAsync(JobNodeId jobNodeId, string name)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO request_holding_area (job_node_id, name, default_priority_id)
							  VALUES (@jobNodeId, @name, @priorityId)
							  RETURNING id;
							  """;
		command.AddParameter("@jobNodeId", jobNodeId.Value);
		command.AddParameter("@name", name);
		command.AddParameter("@priorityId", (short)Priority.Medium);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task SeedJobRequestAsync(JobNodeId jobNodeId, AppUserId requesterUserId, long holdingAreaId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_request (job_node_id, requester_user_id, holding_area_id, submitted_at)
							  VALUES (@jobNodeId, @requesterUserId, @holdingAreaId, @submittedAt);
							  """;
		command.AddParameter("@jobNodeId", jobNodeId.Value);
		command.AddParameter("@requesterUserId", requesterUserId.Value);
		command.AddParameter("@holdingAreaId", holdingAreaId);
		command.AddParameter("@submittedAt", EncodeInstant(DateTimeOffset.UtcNow));

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task SeedJobRequestNoteAsync(JobNodeId jobNodeId, AppUserId authorUserId, string content)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_request_note (job_node_id, author_user_id, content, is_visible_to_requester, created_at)
							  VALUES (@jobNodeId, @authorUserId, @content, @isVisibleToRequester, @createdAt);
							  """;
		command.AddParameter("@jobNodeId", jobNodeId.Value);
		command.AddParameter("@authorUserId", authorUserId.Value);
		command.AddParameter("@content", content);
		command.AddParameter("@isVisibleToRequester", true);
		command.AddParameter("@createdAt", EncodeInstant(DateTimeOffset.UtcNow));

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task SeedNodeRateOverrideAsync(JobNodeId nodeId, AppUserId userId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO node_rate_override (node_id, user_id, effective_start, rate, changed_at)
							  VALUES (@nodeId, @userId, @effectiveStart, @rate, @changedAt);
							  """;
		command.AddParameter("@nodeId", nodeId.Value);
		command.AddParameter("@userId", userId.Value);
		command.AddParameter("@effectiveStart", EncodeInstant(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture)));
		command.AddParameter("@rate", OverrideHourlyRate);
		command.AddParameter("@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<long> CountRowsForNodeAsync(string tableName, string nodeColumnName, JobNodeId nodeId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE {nodeColumnName} = @nodeId;";
		command.AddParameter("@nodeId", nodeId.Value);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<WorkSessionId> SeedWorkSessionAsync(
		JobNodeId leafNodeId, AppUserId workedByUserId, DateTimeOffset startedAt, DateTimeOffset finishedAt)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  VALUES (@leafWorkId, @workedByUserId, @startedAt, @finishedAt, @changedAt)
							  RETURNING id;
							  """;
		command.AddParameter("@leafWorkId", leafNodeId.Value);
		command.AddParameter("@workedByUserId", workedByUserId.Value);
		command.AddParameter("@startedAt", EncodeInstant(startedAt));
		command.AddParameter("@finishedAt", EncodeInstant(finishedAt));
		command.AddParameter("@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

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
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT worked_by_user_id, started_at, finished_at FROM work_session WHERE id = @sessionId;";
		command.AddParameter("@sessionId", sessionId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		return (reader.GetInt64(0), reader.GetValue(1), reader.IsDBNull(2) ? null : reader.GetValue(2));
	}

	private async Task<(JobNodeId LeafWorkId, string? FullCriteria)> ReadWorkSessionLeafWorkAsync(WorkSessionId sessionId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT lw.job_node_id, lw.full_criteria
							  FROM work_session ws
							  JOIN leaf_work lw ON lw.job_node_id = ws.leaf_work_id
							  WHERE ws.id = @sessionId;
							  """;
		command.AddParameter("@sessionId", sessionId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		var leafWorkId = new JobNodeId(reader.GetInt64(0));
		var fullCriteria = reader.IsDBNull(1) ? null : reader.GetString(1);
		return (leafWorkId, fullCriteria);
	}



	private async Task SetActorAccountStateAsync(AppUserId appUserId, bool isEnabled, DateTimeOffset? lockoutEnd)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE identity_user SET is_enabled = @isEnabled, lockout_end = @lockoutEnd WHERE app_user_id = @appUserId;";
		command.AddParameter("@isEnabled", isEnabled);
		command.AddParameter("@lockoutEnd", lockoutEnd is null ? DBNull.Value : EncodeInstant(lockoutEnd.Value));
		command.AddParameter("@appUserId", appUserId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}
}
