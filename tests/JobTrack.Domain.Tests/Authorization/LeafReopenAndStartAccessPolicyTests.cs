namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class LeafReopenAndStartAccessPolicyTests
{
	private static readonly AppUserId Actor = new(1);
	private static readonly AppUserId OtherWorker = new(2);

	[Fact]
	public void An_administrator_may_reopen_and_start_for_any_target_with_no_control_or_participation() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Administrator],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = false,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = OtherWorker,
										  })
									  .Should().BeTrue();

	[Fact]
	public void A_job_manager_may_reopen_and_start_for_any_target_with_no_control_or_participation() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.JobManager],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = false,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = OtherWorker,
										  })
									  .Should().BeTrue();

	[Fact]
	public void A_controlling_worker_may_reopen_and_start_for_any_target() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Worker],
										  new() {
											  ActorControlsNode = true,
											  ActorParticipatedPreviously = false,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = OtherWorker,
										  })
									  .Should().BeTrue();

	[Fact]
	public void A_controlling_worker_may_reopen_and_start_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Worker],
										  new() {
											  ActorControlsNode = true,
											  ActorParticipatedPreviously = false,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = Actor,
										  })
									  .Should().BeTrue();

	[Fact]
	public void A_prior_participant_with_no_control_may_reopen_and_start_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Worker],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = true,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = Actor,
										  })
									  .Should().BeTrue();

	[Fact]
	public void A_prior_participant_with_no_control_may_not_start_for_a_different_worker() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Worker],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = true,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = OtherWorker,
										  })
									  .Should().BeFalse();

	[Fact]
	public void A_non_participant_non_controlling_worker_may_not_reopen_and_start_at_all() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Worker],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = false,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = Actor,
										  })
									  .Should().BeFalse();

	[Fact]
	public void An_actor_with_no_roles_may_never_reopen_and_start_even_as_a_prior_participant_starting_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = true,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = Actor,
										  })
									  .Should().BeFalse();

	[Fact]
	public void A_requester_may_never_reopen_and_start_even_as_a_prior_participant_starting_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Requester],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = true,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = Actor,
										  })
									  .Should().BeFalse();

	[Fact]
	public void A_controlling_worker_who_is_also_a_prior_participant_may_still_start_for_another_worker() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Worker],
										  new() {
											  ActorControlsNode = true,
											  ActorParticipatedPreviously = true,
											  ActorUserId = Actor,
											  TargetWorkedByUserId = OtherWorker,
										  })
									  .Should().BeTrue();

	[Fact]
	public void Default_unspecified_user_identifiers_retain_current_equality_based_behavior() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
										  [EmployeeRole.Worker],
										  new() {
											  ActorControlsNode = false,
											  ActorParticipatedPreviously = true,
											  ActorUserId = default,
											  TargetWorkedByUserId = default,
										  })
									  .Should().BeTrue();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
			null!,
			new() {
				ActorControlsNode = false,
				ActorParticipatedPreviously = true,
				ActorUserId = Actor,
				TargetWorkedByUserId = Actor,
			});

		act.Should().Throw<ArgumentNullException>().WithParameterName("actorRoles");
	}

	[Fact]
	public void A_null_facts_record_is_rejected()
	{
		var act = () => LeafReopenAndStartAccessPolicy.CanReopenAndStartFor([EmployeeRole.Worker], null!);

		act.Should().Throw<ArgumentNullException>().WithParameterName("facts");
	}
}
