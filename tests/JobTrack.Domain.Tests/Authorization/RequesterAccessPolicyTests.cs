namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class RequesterAccessPolicyTests
{
	[Fact]
	public void A_requester_may_submit_to_an_active_holding_area_they_are_eligible_for()
	{
		RequesterAccessPolicy.CanSubmit([EmployeeRole.Requester], true, true)
			.Should().BeTrue();
	}

	[Fact]
	public void A_requester_may_not_submit_to_an_inactive_holding_area()
	{
		RequesterAccessPolicy.CanSubmit([EmployeeRole.Requester], false, true)
			.Should().BeFalse();
	}

	[Fact]
	public void A_requester_may_not_submit_to_a_holding_area_they_are_not_eligible_for()
	{
		RequesterAccessPolicy.CanSubmit([EmployeeRole.Requester], true, false)
			.Should().BeFalse();
	}

	[Theory]
	[InlineData(EmployeeRole.Administrator)]
	[InlineData(EmployeeRole.JobManager)]
	[InlineData(EmployeeRole.Worker)]
	public void An_operational_role_without_Requester_may_not_submit_a_request(EmployeeRole role) =>
		RequesterAccessPolicy.CanSubmit([role], true, true).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected_by_CanSubmit()
	{
		var act = () => RequesterAccessPolicy.CanSubmit(null!, true, true);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void The_requester_who_submitted_the_request_may_view_it()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.Requester],
			true,
			false,
			false,
			false).Should().BeTrue();
	}

	[Fact]
	public void A_different_requester_may_not_view_the_request_when_department_visibility_is_disabled()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.Requester],
			false,
			false,
			true,
			false).Should().BeFalse();
	}

	[Fact]
	public void A_requester_sharing_the_department_may_view_the_request_when_department_visibility_is_enabled()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.Requester],
			false,
			true,
			true,
			false).Should().BeTrue();
	}

	[Fact]
	public void A_requester_in_a_different_department_may_not_view_the_request_even_with_department_visibility_enabled()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.Requester],
			false,
			true,
			false,
			false).Should().BeFalse();
	}

	[Fact]
	public void An_administrator_may_view_any_request()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.Administrator],
			false,
			false,
			false,
			false).Should().BeTrue();
	}

	[Fact]
	public void A_job_manager_may_view_any_request()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.JobManager],
			false,
			false,
			false,
			false).Should().BeTrue();
	}

	[Fact]
	public void A_worker_who_controls_the_anchor_node_may_view_the_request()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.Worker],
			false,
			false,
			false,
			true).Should().BeTrue();
	}

	[Fact]
	public void A_worker_who_does_not_control_the_anchor_node_may_not_view_the_request()
	{
		RequesterAccessPolicy.CanView(
			[EmployeeRole.Worker],
			false,
			false,
			false,
			false).Should().BeFalse();
	}

	[Fact]
	public void A_null_role_collection_is_rejected_by_CanView()
	{
		var act = () => RequesterAccessPolicy.CanView(
			null!, true, false, false,
			false);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void The_requesting_requester_may_comment_on_their_own_open_request()
	{
		RequesterAccessPolicy.CanCommentAsRequester(
			[EmployeeRole.Requester],
			true,
			false,
			false,
			false,
			false).Should().BeTrue();
	}

	[Fact]
	public void The_requesting_requester_may_not_comment_on_a_closed_request()
	{
		RequesterAccessPolicy.CanCommentAsRequester(
			[EmployeeRole.Requester],
			true,
			false,
			false,
			false,
			true).Should().BeFalse();
	}

	[Fact]
	public void A_controlling_worker_who_can_view_the_request_may_not_comment_as_requester()
	{
		RequesterAccessPolicy.CanCommentAsRequester(
			[EmployeeRole.Worker],
			false,
			false,
			false,
			true,
			false).Should().BeFalse();
	}

	[Fact]
	public void A_null_role_collection_is_rejected_by_CanCommentAsRequester()
	{
		var act = () => RequesterAccessPolicy.CanCommentAsRequester(
			null!, true, false, false,
			false, false);

		act.Should().Throw<ArgumentNullException>();
	}
}
