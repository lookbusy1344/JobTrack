namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Microsoft.EntityFrameworkCore;
using NodaTime;

public abstract partial class WorkSessionCommandPortContractTestsBase
{
	[Fact]
	public async Task Pausing_a_leaf_with_a_finish_instant_in_the_future_throws_an_invariant_violation()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.PauseLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			FinishedAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromHours(1),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-finish-in-future");
	}

	[Fact]
	public async Task Completing_a_leaf_with_a_write_up_change_applies_it_in_the_same_commit()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			WriteUpChange = new() {
				NodeVersion = 1,
				WriteUp = "All done",
			},
		});

		result.WriteUpChanged.Should().BeTrue();
		result.Node.Should().NotBeNull();
		result.Node!.WriteUp.Should().Be("All done");
		result.Node.Version.Should().Be(2);
	}

	[Fact]
	public async Task Completing_a_leaf_with_an_unchanged_write_up_text_reports_no_change_and_does_not_burn_a_node_version()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			WriteUpChange = new() {
				NodeVersion = 1,
				WriteUp = null,
			},
		});

		result.WriteUpChanged.Should().BeFalse();
		result.Node!.Version.Should().Be(1);
	}

	[Fact]
	public async Task Completing_a_leaf_with_a_stale_node_version_in_its_write_up_change_throws_a_concurrency_conflict_and_rolls_back_the_completion()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			WriteUpChange = new() {
				NodeVersion = 99,
				WriteUp = "All done",
			},
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var leafAudit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "leaf_work",
				EntityId = leafId.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);
		leafAudit.Events.Should().NotContain(
			e => e.Operation == "set-achievement" && e.Reason != "Advanced automatically on session start",
			"the rejected write-up change must roll back the completion's own achievement transition too");
	}

	[Fact]
	public async Task Finishing_a_session_with_a_write_up_change_applies_both_in_one_commit()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.FinishSessionAndUpdateWriteUpAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
			WriteUpChange = new() {
				NodeVersion = 1,
				WriteUp = "Paused for lunch",
			},
		});

		result.Session.FinishedAt.Should().NotBeNull();
		result.WriteUpChanged.Should().BeTrue();
		result.Node!.WriteUp.Should().Be("Paused for lunch");
		result.Node.Version.Should().Be(2);
	}

	[Fact]
	public async Task Finishing_a_session_with_no_write_up_change_leaves_the_node_version_untouched()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.FinishSessionAndUpdateWriteUpAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
		});

		result.WriteUpChanged.Should().BeFalse();
		result.Node.Should().BeNull();
	}

	[Fact]
	public async Task Audit_persistence_failure_rolls_back_both_the_session_finish_and_write_up()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(
			new() {
				Context = ContextFor(workerId),
				LeafWorkId = leafId,
				WorkedByUserId = workerId,
			});
		var before = await ReadFinishAndWriteUpStateAsync(leafId, session.Id);
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			await AuditFailureInjection.InstallAsync(connection, Provider);
		}

		var act = () => port.FinishSessionAndUpdateWriteUpAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
			WriteUpChange = new() {
				NodeVersion = 1,
				WriteUp = "Must roll back",
			},
		});

		await act.Should().ThrowAsync<DbUpdateException>();
		(await ReadFinishAndWriteUpStateAsync(leafId, session.Id)).Should().Be(
			before,
			"the session, node, and both audit events are one provider transaction");
	}

	[Fact]
	public async Task Audit_persistence_failure_rolls_back_completion_sessions_achievement_and_write_up()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(
			new() {
				Context = ContextFor(workerId),
				JobNodeId = leafId,
				WorkedByUserId = workerId,
			});
		var before = await ReadCompleteAndWriteUpStateAsync(leafId, session.Id);
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			await AuditFailureInjection.InstallAsync(connection, Provider);
		}

		var act = () => port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			WriteUpChange = new() {
				NodeVersion = 1,
				WriteUp = "Must all roll back",
			},
		});

		await act.Should().ThrowAsync<DbUpdateException>();
		(await ReadCompleteAndWriteUpStateAsync(leafId, session.Id)).Should().Be(
			before,
			"completion's node, session, achievement, and audit writes share one provider transaction");
	}

	/// <summary>
	///     ADR 0045 §5: a worker who may finish their own session (the self-finish exception) but does
	///     not control the node cannot smuggle a write-up change through this composite -- the same
	///     rejection the old two-call web-layer sequence produced via a separate <c>EditAsync</c>.
	/// </summary>
	[Fact]
	public async Task Finishing_a_session_with_a_write_up_change_by_a_worker_who_does_not_control_the_node_is_denied_and_the_session_stays_active()
	{
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		// Owned by the job manager, not the worker, so the worker's session-owner self-finish exception
		// applies but WorkSessionAccessPolicy.CanManage's node-control rule does not.
		var managerOwnedLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Manager-owned leaf",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = managerOwnedLeaf.Id,
		});
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(
			new() {
				Context = ContextFor(jobManagerId),
				LeafWorkId = managerOwnedLeaf.Id,
				WorkedByUserId = workerId,
			});

		var act = () => port.FinishSessionAndUpdateWriteUpAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
			WriteUpChange = new() {
				NodeVersion = 1,
				WriteUp = "Trying to sneak this in",
			},
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var sessionAudit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "work_session",
				EntityId = session.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);
		sessionAudit.Events.Should().NotContain(
			e => e.Operation == "finish-work-session", "the denied write-up change must roll back the session finish too");
	}

	[Fact]
	public async Task A_job_manager_can_reopen_and_start_for_a_target_worker_who_neither_controls_nor_participated()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedTerminalLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.reopen", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = "More work was found",
			WorkedByUserId = otherWorkerId,
		});

		result.Achievement.Should().Be(Achievement.InProgress);
		result.Session.WorkedByUserId.Should().Be(otherWorkerId);
		_ = workerId;
	}

	[Fact]
	public async Task Reopening_and_starting_writes_two_achievement_audit_events_and_one_session_audit_event()
	{
		var (_, jobManagerId, _, leafId) = await SeedTerminalLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = "More work was found",
			WorkedByUserId = jobManagerId,
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var leafAudit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "leaf_work",
				EntityId = leafId.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);
		leafAudit.Events.Should().Contain(e => e.Operation == "set-achievement" && e.Reason == "More work was found");
		leafAudit.Events.Should().Contain(e => e.Operation == "set-achievement" && e.Reason == "Advanced automatically on session start");

		var sessionAudit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "work_session",
				EntityId = result.Session.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);
		sessionAudit.Events.Should().Contain(e => e.Operation == "start-work-session");
	}

	[Fact]
	public async Task A_prior_participant_who_no_longer_controls_the_leaf_can_reopen_and_start_for_themselves()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedTerminalLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("New Owner", "new.owner.reopen", EmployeeRole.Worker);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leafId,
			Description = "Reassigned away from the original worker",
			OwnerUserId = otherWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = "Work resumed",
			WorkedByUserId = workerId,
		});

		result.Achievement.Should().Be(Achievement.InProgress);
		result.Session.WorkedByUserId.Should().Be(workerId);
	}

	[Fact]
	public async Task A_prior_participant_who_no_longer_controls_the_leaf_cannot_start_for_a_different_worker()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedTerminalLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("New Owner", "new.owner.reopen2", EmployeeRole.Worker);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leafId,
			Description = "Reassigned away from the original worker",
			OwnerUserId = otherWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = "Trying to hand it off",
			WorkedByUserId = otherWorkerId,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_non_participant_non_controlling_worker_cannot_reopen_and_start_at_all()
	{
		var (_, _, _, leafId) = await SeedTerminalLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Bystander", "bystander.reopen", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(otherWorkerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = "Trying anyway",
			WorkedByUserId = otherWorkerId,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Reopening_an_archived_leaf_throws_an_invariant_violation()
	{
		var (_, jobManagerId, _, leafId) = await SeedTerminalLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.ArchiveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leafId,
			Version = 1,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = "Trying anyway",
			WorkedByUserId = jobManagerId,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-leaf-closed");
	}

	[Fact]
	public async Task Reopening_and_starting_a_leaf_blocked_by_an_unsatisfied_prerequisite_rolls_back()
	{
		var (rootId, jobManagerId, workerId, terminalLeafId) = await SeedTerminalLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var unsatisfiedRequiredLeafId = await CreateReadyLeafAsync(jobNodePort, rootId, jobManagerId, workerId);
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = unsatisfiedRequiredLeafId,
			DependentJobId = terminalLeafId,
		});
		var sessionPort = CreateSessionPort(database.ConnectionString);

		var act = () => sessionPort.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = terminalLeafId,
			Version = 3,
			Reason = "Trying while blocked",
			WorkedByUserId = workerId,
		});

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();

		await jobNodePort.RemovePrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = unsatisfiedRequiredLeafId,
			DependentJobId = terminalLeafId,
		});
		var result = await sessionPort.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = terminalLeafId,
			Version = 3,
			Reason = "Blocker removed",
			WorkedByUserId = workerId,
		});
		result.Achievement.Should().Be(Achievement.InProgress, "the rejected attempt must leave the terminal leaf version and state unchanged");
	}

	/// <summary>
	///     ADR 0051: reopening a successful prerequisite is permitted even while a dependent's session is
	///     running. The reopen is never rejected for that reason -- the consequence lands on the dependent,
	///     which becomes blocked and stays that way until the prerequisite succeeds again.
	/// </summary>
	[Fact]
	public async Task Reopening_a_successful_prerequisite_is_permitted_while_a_dependent_session_is_live()
	{
		var seeded = await SeedSuccessfulPrerequisiteWithLiveDependentAsync();
		var sessionPort = CreateSessionPort(database.ConnectionString);

		var result = await sessionPort.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(seeded.JobManagerId),
			JobNodeId = seeded.RequiredLeafId,
			Version = 3,
			Reason = "Closed by mistake",
			WorkedByUserId = seeded.WorkerId,
		});

		result.Achievement.Should().Be(Achievement.InProgress);
		(await ReadLeafAchievementAsync(seeded.DependentLeafId)).Should().Be((short)Achievement.InProgress,
			"reopening a prerequisite never reaches into the dependent's own achievement");
		(await ReadLeafSessionStateAsync(seeded.DependentLeafId)).Should().ContainSingle()
																 .Which.IsActive.Should().BeTrue("the dependent's running session is left alone, not ended behind the worker's back");
	}

	/// <summary>
	///     ADR 0051's other half: the dependent's live session may continue, but the leaf cannot be closed
	///     while the prerequisite it depends on is open again. This is the same
	///     <see cref="PrerequisiteBlockedException" /> an unsatisfied prerequisite has always raised --
	///     asserted here for the specific state a reopen creates, where the dependent was ready when its
	///     session started.
	/// </summary>
	[Fact]
	public async Task A_dependent_with_a_live_session_cannot_be_completed_while_its_prerequisite_is_reopened()
	{
		var seeded = await SeedSuccessfulPrerequisiteWithLiveDependentAsync();
		var sessionPort = CreateSessionPort(database.ConnectionString);
		_ = await sessionPort.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(seeded.JobManagerId),
			JobNodeId = seeded.RequiredLeafId,
			Version = 3,
			Reason = "Closed by mistake",
			WorkedByUserId = seeded.WorkerId,
		});

		var act = () => sessionPort.CompleteLeafAsync(new() {
			Context = ContextFor(seeded.JobManagerId),
			JobNodeId = seeded.DependentLeafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = seeded.DependentSessionId, Version = seeded.DependentSessionVersion,
				},
			],
		});

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();
		(await ReadLeafAchievementAsync(seeded.DependentLeafId)).Should().Be((short)Achievement.InProgress);
		(await ReadLeafSessionStateAsync(seeded.DependentLeafId)).Should().ContainSingle()
																 .Which.IsActive.Should().BeTrue("the rejected completion must not finish the session it would have closed");
	}

	/// <summary>
	///     The block is a state, not a punishment: once the reopened prerequisite reaches
	///     <see cref="Achievement.Success" /> again, the dependent completes normally with the same
	///     session it has been holding open throughout.
	/// </summary>
	[Fact]
	public async Task A_dependent_can_be_completed_once_its_reopened_prerequisite_succeeds_again()
	{
		var seeded = await SeedSuccessfulPrerequisiteWithLiveDependentAsync();
		var sessionPort = CreateSessionPort(database.ConnectionString);
		var reopened = await sessionPort.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(seeded.JobManagerId),
			JobNodeId = seeded.RequiredLeafId,
			Version = 3,
			Reason = "Closed by mistake",
			WorkedByUserId = seeded.WorkerId,
		});
		_ = await sessionPort.CompleteLeafAsync(new() {
			Context = ContextFor(seeded.JobManagerId),
			JobNodeId = seeded.RequiredLeafId,
			Version = reopened.Version,
			ExpectedActiveSessions = [
				new() {
					Id = reopened.Session.Id, Version = reopened.Session.Version,
				},
			],
			FinalAchievement = Achievement.Success,
		});

		var result = await sessionPort.CompleteLeafAsync(new() {
			Context = ContextFor(seeded.JobManagerId),
			JobNodeId = seeded.DependentLeafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = seeded.DependentSessionId, Version = seeded.DependentSessionVersion,
				},
			],
		});

		result.Achievement.Should().Be(Achievement.Success);
	}

	/// <summary>
	///     Seeds the ADR 0051 shape: a leaf closed as <see cref="Achievement.Success" /> (version 3, as
	///     <see cref="SeedTerminalLeafAsync" />), a second leaf that requires it, and a session running on
	///     that dependent right now -- the state in which reopening the prerequisite used to be rejected.
	/// </summary>
	private async Task<(AppUserId JobManagerId, AppUserId WorkerId, JobNodeId RequiredLeafId, JobNodeId DependentLeafId,
		WorkSessionId DependentSessionId, long DependentSessionVersion)> SeedSuccessfulPrerequisiteWithLiveDependentAsync()
	{
		var (rootId, jobManagerId, workerId, requiredLeafId) = await SeedReadyLeafAsync();
		var sessionPort = CreateSessionPort(database.ConnectionString);
		var requiredSession = await sessionPort.StartWorkAsync(
			new() {
				Context = ContextFor(workerId),
				JobNodeId = requiredLeafId,
				WorkedByUserId = workerId,
			});
		_ = await sessionPort.FinishSessionAsync(
			new() {
				Context = ContextFor(workerId),
				SessionId = requiredSession.Id,
				Version = requiredSession.Version,
			});
		_ = await CreateAchievementPort(database.ConnectionString).SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = requiredLeafId,
			NewAchievement = Achievement.Success,
			Reason = "Ready for dependent work",
			Version = 2,
		});

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var dependentLeafId = await CreateReadyLeafAsync(jobNodePort, rootId, jobManagerId, workerId);
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = requiredLeafId,
			DependentJobId = dependentLeafId,
		});
		var dependentSession = await sessionPort.StartWorkAsync(
			new() {
				Context = ContextFor(workerId),
				JobNodeId = dependentLeafId,
				WorkedByUserId = workerId,
			});

		return (jobManagerId, workerId, requiredLeafId, dependentLeafId, dependentSession.Id, dependentSession.Version);
	}

	[Fact]
	public async Task Reopening_and_starting_with_a_blank_reason_is_rejected_without_changing_the_terminal_leaf()
	{
		var (_, jobManagerId, _, leafId) = await SeedTerminalLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = " ",
			WorkedByUserId = jobManagerId,
		});

		await act.Should().ThrowAsync<ArgumentException>().WithParameterName("Reason");

		var result = await port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 3,
			Reason = "More work was found",
			WorkedByUserId = jobManagerId,
		});
		result.Achievement.Should().Be(Achievement.InProgress, "the rejected request must leave the terminal version and state unchanged");
	}

	[Fact]
	public async Task Reopening_and_starting_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, jobManagerId, _, leafId) = await SeedTerminalLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.ReopenAndStartWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 99,
			Reason = "Trying again",
			WorkedByUserId = jobManagerId,
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	/// <summary>
	///     Extends <see cref="SeedReadyLeafAsync" />: starts and finishes one session for
	///     <c>WorkerId</c>, then records <see cref="Achievement.Unsuccessful" /> (leaf version 3 after
	///     attach/auto-advance/terminal-transition), giving reopen-and-start tests a genuine prior
	///     participant plus a real terminal leaf rather than a direct schema bypass.
	/// </summary>
	private async Task<(JobNodeId RootId, AppUserId JobManagerId, AppUserId WorkerId, JobNodeId LeafId)> SeedTerminalLeafAsync()
	{
		var (rootId, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});
		_ = await port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
		});
		var achievementPort = CreateAchievementPort(database.ConnectionString);
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not work out",
			Version = 2,
		});

		return (rootId, jobManagerId, workerId, leafId);
	}

	private async Task SetAchievementIdAsync(JobNodeId leafId, short achievementId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE leaf_work SET achievement_id = @achievementId WHERE job_node_id = @leafId;";
		command.AddParameter("@achievementId", achievementId);
		command.AddParameter("@leafId", leafId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IWorkSessionCommandPort CreateSessionPort(string connectionString);

	internal abstract IWorkSessionCommandPort CreateSessionPort(string connectionString, IClock clock);

	internal abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	protected static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	/// <summary>
	///     Runs remediation plan §2.1's provider race between a compound session-finish/write-up
	///     command and an independent full node edit from the same starting node version.
	/// </summary>
	protected async Task AssertConcurrentFinishWithWriteUpVersusNodeEditAsync()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var session = await CreateSessionPort(database.ConnectionString).StartSessionAsync(
			new() {
				Context = ContextFor(workerId),
				LeafWorkId = leafId,
				WorkedByUserId = workerId,
			});

		var results = await Task.WhenAll(
			TryFinishWithWriteUpAsync(
				CreateSessionPort(database.ConnectionString), workerId, session.Id, session.Version),
			TryConcurrentNodeEditAsync(
				CreateJobNodePort(database.ConnectionString), workerId, leafId));

		results.Count(succeeded => succeeded).Should().Be(1);
		var state = await ReadFinishAndWriteUpStateAsync(leafId, session.Id);
		state.Should().Be(results[0]
			? new FinishAndWriteUpState("Compound write-up", 2, true, 2)
			: new FinishAndWriteUpState("Concurrent edit", 2, false, 1));
	}

	/// <summary>
	///     Runs remediation plan §2.1's provider race between the compound command and a concurrent
	///     standalone finish of the same session.
	/// </summary>
	protected async Task AssertConcurrentFinishWithWriteUpVersusSessionFinishAsync()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var session = await CreateSessionPort(database.ConnectionString).StartSessionAsync(
			new() {
				Context = ContextFor(workerId),
				LeafWorkId = leafId,
				WorkedByUserId = workerId,
			});

		var results = await Task.WhenAll(
			TryFinishWithWriteUpAsync(
				CreateSessionPort(database.ConnectionString), workerId, session.Id, session.Version),
			TryStandaloneFinishAsync(
				CreateSessionPort(database.ConnectionString), workerId, session.Id, session.Version));

		results.Count(succeeded => succeeded).Should().Be(1);
		var state = await ReadFinishAndWriteUpStateAsync(leafId, session.Id);
		state.Should().Be(results[0]
			? new FinishAndWriteUpState("Compound write-up", 2, true, 2)
			: new FinishAndWriteUpState(null, 1, true, 2));
	}

	/// <summary>
	///     The pause composite's own race: two callers pause the same two-worker leaf from the same read
	///     of its active set. Exactly one must win -- the loser's confirmed set is stale by version, so it
	///     must conflict rather than re-finishing an already-finished session at a second instant.
	/// </summary>
	protected async Task AssertConcurrentPauseVersusPauseAsync()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var mateId = await SeedEmployeeAsync("Pause Race Mate", "pause.race.mate", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var first = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});
		var second = await port.StartSessionAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafWorkId = leafId,
			WorkedByUserId = mateId,
		});
		ExpectedActiveSession[] confirmed = [
			new() {
				Id = first.Id, Version = first.Version,
			},
			new() {
				Id = second.Id, Version = second.Version,
			},
		];

		var results = await Task.WhenAll(
			TryPauseAsync(CreateSessionPort(database.ConnectionString), jobManagerId, leafId, confirmed),
			TryPauseAsync(CreateSessionPort(database.ConnectionString), jobManagerId, leafId, confirmed));

		results.Count(succeeded => succeeded).Should().Be(1);
		var stored = await ReadLeafSessionStateAsync(leafId);
		stored.Should().HaveCount(2).And.OnlyContain(row => !row.IsActive);
		stored.Select(row => row.FinishedAt).Distinct().Should()
			  .ContainSingle("the winning pause stops every clock at one instant");
	}

	private static async Task<bool> TryPauseAsync(
		IWorkSessionCommandPort port, AppUserId actorId, JobNodeId leafId, IReadOnlyList<ExpectedActiveSession> confirmed)
	{
		try {
			_ = await port.PauseLeafAsync(new() {
				Context = ContextFor(actorId),
				JobNodeId = leafId,
				ExpectedActiveSessions = [.. confirmed],
			});
			return true;
		}
		catch (ConcurrencyConflictException) {
			return false;
		}
	}

	private static async Task<bool> TryFinishWithWriteUpAsync(
		IWorkSessionCommandPort port,
		AppUserId actorId,
		WorkSessionId sessionId,
		long sessionVersion)
	{
		try {
			_ = await port.FinishSessionAndUpdateWriteUpAsync(new() {
				Context = ContextFor(actorId),
				SessionId = sessionId,
				Version = sessionVersion,
				WriteUpChange = new() {
					NodeVersion = 1,
					WriteUp = "Compound write-up",
				},
			});
			return true;
		}
		catch (ConcurrencyConflictException) {
			return false;
		}
	}

	private static async Task<bool> TryConcurrentNodeEditAsync(
		IJobNodeCommandPort port,
		AppUserId actorId,
		JobNodeId leafId)
	{
		try {
			_ = await port.EditAsync(new() {
				Context = ContextFor(actorId),
				NodeId = leafId,
				Description = "Do the thing",
				WriteUp = "Concurrent edit",
				OwnerUserId = actorId,
				Priority = Priority.Medium,
				Version = 1,
			});
			return true;
		}
		catch (ConcurrencyConflictException) {
			return false;
		}
	}

	private static async Task<bool> TryStandaloneFinishAsync(
		IWorkSessionCommandPort port,
		AppUserId actorId,
		WorkSessionId sessionId,
		long sessionVersion)
	{
		try {
			_ = await port.FinishSessionAsync(new() {
				Context = ContextFor(actorId),
				SessionId = sessionId,
				Version = sessionVersion,
			});
			return true;
		}
		catch (ConcurrencyConflictException) {
			return false;
		}
	}

	private static async Task<JobNodeId> CreateReadyLeafAsync(
		IJobNodeCommandPort jobNodePort, JobNodeId parentId, AppUserId jobManagerId, AppUserId workerId)
	{
		var leaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = parentId,
			Description = "Do the thing",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(
			new() {
				Context = ContextFor(jobManagerId),
				JobNodeId = leaf.Id,
			});

		return leaf.Id;
	}

	/// <summary>
	///     Seeds a deployed schema, an administrator/root via the real bootstrap port (with the
	///     administrator additionally granted <see cref="EmployeeRole.JobManager" />, since bootstrap
	///     itself assigns no roles), one <see cref="EmployeeRole.Worker" /> employee, and a leaf with
	///     <c>LeafWork</c> attached and ready (no prerequisites). Exposed (rather than private) so a
	///     provider-specific subclass can add its own concurrency/race tests (plan §6) reusing the same
	///     seeding.
	/// </summary>
	protected async Task<(JobNodeId RootId, AppUserId JobManagerId, AppUserId WorkerId, JobNodeId LeafId)> SeedReadyLeafAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
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

		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			await DatabaseContractTestSupport.AssignRoleAsync(connection, result.AdministratorId, EmployeeRole.JobManager);
		}

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.session", EmployeeRole.Worker);

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leafId = await CreateReadyLeafAsync(jobNodePort, result.RootJobNodeId, result.AdministratorId, workerId);

		return (result.RootJobNodeId, result.AdministratorId, workerId, leafId);
	}

	/// <summary>Exposed so a provider-specific subclass can seed a second worker for its own concurrency/race tests (plan §6).</summary>
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
	///     Every session on <paramref name="leafId" />, as (id, whether it is still active, its one
	///     finish instant as text) ordered by id -- what a pause has to leave behind: nobody clocked on,
	///     every clock stopped at the same instant.
	/// </summary>
	private async Task<List<(long SessionId, bool IsActive, string? FinishedAt)>> ReadLeafSessionStateAsync(JobNodeId leafId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT ws.id,
							         CASE WHEN ws.finished_at IS NULL THEN 1 ELSE 0 END,
							         CAST(ws.finished_at AS TEXT)
							  FROM work_session ws
							  WHERE ws.leaf_work_id = @leafId
							  ORDER BY ws.id;
							  """;
		command.AddParameter("@leafId", leafId.Value);
		await using var reader = await command.ExecuteReaderAsync();

		var rows = new List<(long, bool, string?)>();
		while (await reader.ReadAsync()) {
			rows.Add((
				Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
				Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture),
				reader.IsDBNull(2) ? null : reader.GetString(2)));
		}

		return rows;
	}

	/// <summary>The leaf's current achievement id, for proving a pause left it alone.</summary>
	private async Task<short> ReadLeafAchievementAsync(JobNodeId leafId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT lw.achievement_id FROM leaf_work lw WHERE lw.job_node_id = @leafId;";
		command.AddParameter("@leafId", leafId.Value);

		return Convert.ToInt16(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<FinishAndWriteUpState> ReadFinishAndWriteUpStateAsync(
		JobNodeId leafId,
		WorkSessionId sessionId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT jn.write_up,
							         jn.row_version,
							         CASE WHEN ws.finished_at IS NULL THEN 0 ELSE 1 END,
							         ws.row_version
							  FROM job_node jn
							  JOIN work_session ws ON ws.leaf_work_id = jn.id
							  WHERE jn.id = @leafId AND ws.id = @sessionId;
							  """;
		command.AddParameter("@leafId", leafId.Value);
		command.AddParameter("@sessionId", sessionId.Value);
		await using var reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue();

		return new(
			reader.IsDBNull(0) ? null : reader.GetString(0),
			Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
			Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture),
			Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture));
	}

	private async Task<CompleteAndWriteUpState> ReadCompleteAndWriteUpStateAsync(
		JobNodeId leafId,
		WorkSessionId sessionId)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT jn.write_up,
							         jn.row_version,
							         CASE WHEN ws.finished_at IS NULL THEN 0 ELSE 1 END,
							         ws.row_version,
							         lw.achievement_id,
							         lw.row_version
							  FROM job_node jn
							  JOIN leaf_work lw ON lw.job_node_id = jn.id
							  JOIN work_session ws ON ws.leaf_work_id = lw.job_node_id
							  WHERE jn.id = @leafId AND ws.id = @sessionId;
							  """;
		command.AddParameter("@leafId", leafId.Value);
		command.AddParameter("@sessionId", sessionId.Value);
		await using var reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue();

		return new(
			reader.IsDBNull(0) ? null : reader.GetString(0),
			Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
			Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture),
			Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
			Convert.ToInt16(reader.GetValue(4), CultureInfo.InvariantCulture),
			Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture));
	}



	private sealed record FinishAndWriteUpState(
		string? WriteUp,
		long NodeVersion,
		bool SessionIsFinished,
		long SessionVersion);

	private sealed record CompleteAndWriteUpState(
		string? WriteUp,
		long NodeVersion,
		bool SessionIsFinished,
		long SessionVersion,
		short AchievementId,
		long LeafWorkVersion);
}
