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
				[EmployeeRole.Administrator], false, false, Actor, OtherWorker)
			.Should().BeTrue();

	[Fact]
	public void A_job_manager_may_reopen_and_start_for_any_target_with_no_control_or_participation() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.JobManager], false, false, Actor, OtherWorker)
			.Should().BeTrue();

	[Fact]
	public void A_controlling_worker_may_reopen_and_start_for_any_target() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.Worker], true, false, Actor, OtherWorker)
			.Should().BeTrue();

	[Fact]
	public void A_controlling_worker_may_reopen_and_start_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.Worker], true, false, Actor, Actor)
			.Should().BeTrue();

	[Fact]
	public void A_prior_participant_with_no_control_may_reopen_and_start_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.Worker], false, true, Actor, Actor)
			.Should().BeTrue();

	[Fact]
	public void A_prior_participant_with_no_control_may_not_start_for_a_different_worker() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.Worker], false, true, Actor, OtherWorker)
			.Should().BeFalse();

	[Fact]
	public void A_non_participant_non_controlling_worker_may_not_reopen_and_start_at_all() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.Worker], false, false, Actor, Actor)
			.Should().BeFalse();

	[Fact]
	public void An_actor_with_no_roles_may_never_reopen_and_start_even_as_a_prior_participant_starting_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[], false, true, Actor, Actor)
			.Should().BeFalse();

	[Fact]
	public void A_requester_may_never_reopen_and_start_even_as_a_prior_participant_starting_for_themselves() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.Requester], false, true, Actor, Actor)
			.Should().BeFalse();

	[Fact]
	public void A_controlling_worker_who_is_also_a_prior_participant_may_still_start_for_another_worker() =>
		LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				[EmployeeRole.Worker], true, true, Actor, OtherWorker)
			.Should().BeTrue();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
			null!, false, true, Actor, Actor);

		act.Should().Throw<ArgumentNullException>();
	}
}
