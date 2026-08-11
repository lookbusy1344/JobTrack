namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class AuditAccessPolicyTests
{
	[Fact]
	public void An_auditor_may_search_audit_history() => AuditAccessPolicy.CanSearch([EmployeeRole.Auditor]).Should().BeTrue();

	[Fact]
	public void An_administrator_may_search_audit_history() => AuditAccessPolicy.CanSearch([EmployeeRole.Administrator]).Should().BeTrue();

	[Fact]
	public void A_worker_may_not_search_audit_history() => AuditAccessPolicy.CanSearch([EmployeeRole.Worker]).Should().BeFalse();

	[Fact]
	public void A_cost_viewer_alone_may_not_search_audit_history() => AuditAccessPolicy.CanSearch([EmployeeRole.CostViewer]).Should().BeFalse();

	[Fact]
	public void A_requester_may_not_search_audit_history() => AuditAccessPolicy.CanSearch([EmployeeRole.Requester]).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => AuditAccessPolicy.CanSearch(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
