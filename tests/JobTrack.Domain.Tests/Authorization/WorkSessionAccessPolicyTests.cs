namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class WorkSessionAccessPolicyTests
{
	[Fact]
	public void An_administrator_may_manage_a_session_on_a_node_they_do_not_control() =>
		WorkSessionAccessPolicy.CanManage([EmployeeRole.Administrator], false).Should().BeTrue();

	[Fact]
	public void A_job_manager_may_manage_a_session_on_a_node_they_do_not_control() =>
		WorkSessionAccessPolicy.CanManage([EmployeeRole.JobManager], false).Should().BeTrue();

	[Fact]
	public void A_worker_who_controls_the_node_may_manage_a_session_for_any_worker() =>
		WorkSessionAccessPolicy.CanManage([EmployeeRole.Worker], true).Should().BeTrue();

	[Fact]
	public void A_worker_who_does_not_control_the_node_may_not_manage_a_session_even_their_own() =>
		WorkSessionAccessPolicy.CanManage([EmployeeRole.Worker], false).Should().BeFalse();

	[Fact]
	public void An_actor_with_no_roles_may_not_manage_a_session_even_on_a_node_they_control() =>
		WorkSessionAccessPolicy.CanManage([], true).Should().BeFalse();

	[Fact]
	public void A_requester_may_never_manage_a_session_even_on_a_node_they_posted() =>
		WorkSessionAccessPolicy.CanManage([EmployeeRole.Requester], true).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected_by_CanManage()
	{
		var act = () => WorkSessionAccessPolicy.CanManage(null!, true);

		act.Should().Throw<ArgumentNullException>();
	}

	// Viewing a session list is open to every employee role (spec §7.3: a Worker's baseline authority
	// is "View employees and job data", unqualified — the role's restrictions are all on *managing*).
	// The pre-existing own-sessions-only rule was carried into CanView by ADR 0032 without its own
	// justification ("preserving the exact pre-existing self-session rule"); ADR 0041 removes it, so
	// CanView no longer takes an isOwnSession argument. CanManage is untouched: seeing another
	// worker's session never implies being able to edit it.
	[Theory]
	[InlineData(EmployeeRole.Administrator)]
	[InlineData(EmployeeRole.JobManager)]
	[InlineData(EmployeeRole.Worker)]
	[InlineData(EmployeeRole.RateManager)]
	[InlineData(EmployeeRole.CostViewer)]
	[InlineData(EmployeeRole.Auditor)]
	public void Any_employee_role_may_view_a_session_list_including_another_workers(EmployeeRole role) =>
		WorkSessionAccessPolicy.CanView([role]).Should().BeTrue();

	[Fact]
	public void An_actor_with_no_roles_may_not_view_a_session_list() => WorkSessionAccessPolicy.CanView([]).Should().BeFalse();

	[Fact]
	public void An_unassigned_role_does_not_grant_viewing_a_session_list() => WorkSessionAccessPolicy.CanView([EmployeeRole.None]).Should().BeFalse();

	[Fact]
	public void A_requester_may_never_view_a_session_list() => WorkSessionAccessPolicy.CanView([EmployeeRole.Requester]).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected_by_CanView()
	{
		var act = () => WorkSessionAccessPolicy.CanView(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	// CanFinishSession (ADR 0045 §5): CanManage's node-control rule still governs finishing another
	// worker's session or finishing without controlling the node, but the worker named on the session
	// may always finish it themselves regardless of control -- the one narrow exception.
	[Fact]
	public void A_worker_who_controls_the_node_may_finish_any_session_on_it() =>
		WorkSessionAccessPolicy.CanFinishSession([EmployeeRole.Worker], true, false).Should().BeTrue();

	[Fact]
	public void A_worker_who_no_longer_controls_the_node_may_still_finish_their_own_session() =>
		WorkSessionAccessPolicy.CanFinishSession([EmployeeRole.Worker], false, true).Should().BeTrue();

	[Fact]
	public void A_worker_who_does_not_control_the_node_may_not_finish_another_workers_session() =>
		WorkSessionAccessPolicy.CanFinishSession([EmployeeRole.Worker], false, false).Should().BeFalse();

	[Fact]
	public void An_actor_with_no_roles_may_not_finish_even_their_own_session() =>
		WorkSessionAccessPolicy.CanFinishSession([], false, true).Should().BeFalse();

	[Fact]
	public void A_requester_may_never_finish_even_their_own_session() =>
		WorkSessionAccessPolicy.CanFinishSession([EmployeeRole.Requester], false, true).Should().BeFalse();

	[Fact]
	public void An_administrator_may_finish_any_session_without_controlling_the_node() =>
		WorkSessionAccessPolicy.CanFinishSession([EmployeeRole.Administrator], false, false).Should().BeTrue();

	[Fact]
	public void A_null_role_collection_is_rejected_by_CanFinishSession()
	{
		var act = () => WorkSessionAccessPolicy.CanFinishSession(null!, false, true);

		act.Should().Throw<ArgumentNullException>();
	}
}
