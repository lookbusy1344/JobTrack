namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class RequestHoldingAreaConfigurationPolicyTests
{
	[Fact]
	public void An_administrator_may_configure_holding_areas() =>
		RequestHoldingAreaConfigurationPolicy.CanConfigure([EmployeeRole.Administrator]).Should().BeTrue();

	[Fact]
	public void A_job_manager_may_configure_holding_areas() =>
		RequestHoldingAreaConfigurationPolicy.CanConfigure([EmployeeRole.JobManager]).Should().BeTrue();

	[Theory]
	[InlineData(EmployeeRole.Worker)]
	[InlineData(EmployeeRole.RateManager)]
	[InlineData(EmployeeRole.CostViewer)]
	[InlineData(EmployeeRole.Auditor)]
	[InlineData(EmployeeRole.Requester)]
	public void No_other_role_may_configure_holding_areas(EmployeeRole role) =>
		RequestHoldingAreaConfigurationPolicy.CanConfigure([role]).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => RequestHoldingAreaConfigurationPolicy.CanConfigure(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
