namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class JobNodeDeletePolicyTests
{
	[Fact]
	public void An_administrator_may_force_delete_a_worked_leaf() =>
		JobNodeDeletePolicy.CanForceDeleteWorkedLeaf([EmployeeRole.Administrator]).Should().BeTrue();

	[Theory]
	[InlineData(EmployeeRole.JobManager)]
	[InlineData(EmployeeRole.Worker)]
	[InlineData(EmployeeRole.RateManager)]
	[InlineData(EmployeeRole.CostViewer)]
	[InlineData(EmployeeRole.Auditor)]
	[InlineData(EmployeeRole.Requester)]
	public void A_non_administrator_role_may_not_force_delete_a_worked_leaf(EmployeeRole role) =>
		JobNodeDeletePolicy.CanForceDeleteWorkedLeaf([role]).Should().BeFalse();

	[Fact]
	public void An_actor_with_no_roles_may_not_force_delete_a_worked_leaf() => JobNodeDeletePolicy.CanForceDeleteWorkedLeaf([]).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => JobNodeDeletePolicy.CanForceDeleteWorkedLeaf(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
