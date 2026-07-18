namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class JobNodeAccessPolicyTests
{
	[Fact]
	public void An_administrator_may_manage_a_node_they_do_not_own() =>
		JobNodeAccessPolicy.CanManage([EmployeeRole.Administrator], false).Should().BeTrue();

	[Fact]
	public void A_job_manager_may_manage_a_node_they_do_not_own() =>
		JobNodeAccessPolicy.CanManage([EmployeeRole.JobManager], false).Should().BeTrue();

	[Fact]
	public void A_worker_who_owns_the_node_or_an_ancestor_may_manage_it() =>
		JobNodeAccessPolicy.CanManage([EmployeeRole.Worker], true).Should().BeTrue();

	[Fact]
	public void A_worker_who_owns_neither_the_node_nor_an_ancestor_may_not_manage_it() =>
		JobNodeAccessPolicy.CanManage([EmployeeRole.Worker], false).Should().BeFalse();

	[Fact]
	public void An_actor_with_no_roles_may_not_manage_a_node_even_if_they_own_it() => JobNodeAccessPolicy.CanManage([], true).Should().BeFalse();

	[Fact]
	public void A_requester_may_never_manage_a_node_even_one_they_posted() =>
		JobNodeAccessPolicy.CanManage([EmployeeRole.Requester], true).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => JobNodeAccessPolicy.CanManage(null!, true);

		act.Should().Throw<ArgumentNullException>();
	}
}
