namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class JobPickupPolicyTests
{
	[Fact]
	public void A_worker_may_pick_up_an_unassigned_node() => JobPickupPolicy.CanPickUp([EmployeeRole.Worker], true).Should().BeTrue();

	[Fact]
	public void A_job_manager_may_pick_up_an_unassigned_node() => JobPickupPolicy.CanPickUp([EmployeeRole.JobManager], true).Should().BeTrue();

	[Fact]
	public void An_administrator_may_pick_up_an_unassigned_node() => JobPickupPolicy.CanPickUp([EmployeeRole.Administrator], true).Should().BeTrue();

	[Fact]
	public void Nobody_may_pick_up_an_already_owned_node()
	{
		JobPickupPolicy.CanPickUp([EmployeeRole.Worker, EmployeeRole.JobManager, EmployeeRole.Administrator], false)
			.Should().BeFalse();
	}

	[Theory]
	[InlineData(EmployeeRole.RateManager)]
	[InlineData(EmployeeRole.CostViewer)]
	[InlineData(EmployeeRole.Auditor)]
	[InlineData(EmployeeRole.Requester)]
	public void A_read_only_role_may_not_pick_up_an_unassigned_node(EmployeeRole role) => JobPickupPolicy.CanPickUp([role], true).Should().BeFalse();

	[Fact]
	public void An_actor_with_no_roles_may_not_pick_up_an_unassigned_node() => JobPickupPolicy.CanPickUp([], true).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => JobPickupPolicy.CanPickUp(null!, true);

		act.Should().Throw<ArgumentNullException>();
	}
}
