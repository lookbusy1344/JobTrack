namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class ScheduleAccessPolicyTests
{
	[Fact]
	public void A_worker_may_manage_their_own_schedule() => ScheduleAccessPolicy.CanManage([EmployeeRole.Worker], true).Should().BeTrue();

	[Fact]
	public void A_worker_may_not_manage_another_employees_schedule() =>
		ScheduleAccessPolicy.CanManage([EmployeeRole.Worker], false).Should().BeFalse();

	[Fact]
	public void An_administrator_may_manage_a_schedule_that_is_not_their_own() =>
		ScheduleAccessPolicy.CanManage([EmployeeRole.Administrator], false).Should().BeTrue();

	[Fact]
	public void A_job_manager_may_not_manage_another_employees_schedule() =>
		ScheduleAccessPolicy.CanManage([EmployeeRole.JobManager], false).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => ScheduleAccessPolicy.CanManage(null!, true);

		act.Should().Throw<ArgumentNullException>();
	}
}
