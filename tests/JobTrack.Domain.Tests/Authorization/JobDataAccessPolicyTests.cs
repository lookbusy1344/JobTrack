namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class JobDataAccessPolicyTests
{
	[Theory]
	[InlineData(EmployeeRole.Administrator)]
	[InlineData(EmployeeRole.JobManager)]
	[InlineData(EmployeeRole.Worker)]
	[InlineData(EmployeeRole.RateManager)]
	[InlineData(EmployeeRole.CostViewer)]
	[InlineData(EmployeeRole.Auditor)]
	public void Every_baseline_operational_role_may_browse_job_data(EmployeeRole role)
	{
		var canBrowse = JobDataAccessPolicy.CanBrowseJobData([role]);

		canBrowse.Should().BeTrue();
	}

	[Fact]
	public void A_requester_only_actor_may_not_browse_job_data()
	{
		var canBrowse = JobDataAccessPolicy.CanBrowseJobData([EmployeeRole.Requester]);

		canBrowse.Should().BeFalse();
	}

	[Fact]
	public void A_requester_combined_with_an_operational_role_may_not_browse()
	{
		var canBrowse = JobDataAccessPolicy.CanBrowseJobData([EmployeeRole.Requester, EmployeeRole.Worker]);

		canBrowse.Should().BeFalse();
	}

	[Fact]
	public void An_actor_with_no_roles_may_not_browse_job_data()
	{
		var canBrowse = JobDataAccessPolicy.CanBrowseJobData([]);

		canBrowse.Should().BeFalse();
	}

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => JobDataAccessPolicy.CanBrowseJobData(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
