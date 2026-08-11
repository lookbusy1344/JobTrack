namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class RateAccessPolicyTests
{
	[Fact]
	public void A_rate_manager_may_manage_rates() => RateAccessPolicy.CanManage([EmployeeRole.RateManager]).Should().BeTrue();

	[Fact]
	public void An_administrator_may_manage_rates() => RateAccessPolicy.CanManage([EmployeeRole.Administrator]).Should().BeTrue();

	[Fact]
	public void A_worker_may_not_manage_rates() => RateAccessPolicy.CanManage([EmployeeRole.Worker]).Should().BeFalse();

	[Fact]
	public void A_job_manager_may_not_manage_rates() => RateAccessPolicy.CanManage([EmployeeRole.JobManager]).Should().BeFalse();

	[Fact]
	public void A_requester_may_not_manage_rates() => RateAccessPolicy.CanManage([EmployeeRole.Requester]).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => RateAccessPolicy.CanManage(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
