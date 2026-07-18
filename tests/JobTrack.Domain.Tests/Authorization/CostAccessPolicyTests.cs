namespace JobTrack.Domain.Tests.Authorization;

using Abstractions;
using AwesomeAssertions;
using Domain.Authorization;

public sealed class CostAccessPolicyTests
{
	// ---- CanViewNodeCost (ADR 0042): per-node redaction inside an already-admitted subtree -------

	private static readonly AppUserId Actor = new(10);
	private static readonly AppUserId OtherWorker = new(11);

	[Fact]
	public void A_cost_viewer_may_view_costs() => CostAccessPolicy.CanView([EmployeeRole.CostViewer], false).Should().BeTrue();

	[Fact]
	public void An_administrator_may_view_costs() => CostAccessPolicy.CanView([EmployeeRole.Administrator], false).Should().BeTrue();

	[Fact]
	public void A_rate_manager_alone_may_not_view_costs() => CostAccessPolicy.CanView([EmployeeRole.RateManager], false).Should().BeFalse();

	[Fact]
	public void A_worker_may_not_view_costs() => CostAccessPolicy.CanView([EmployeeRole.Worker], false).Should().BeFalse();

	[Fact]
	public void A_requester_may_not_view_costs() => CostAccessPolicy.CanView([EmployeeRole.Requester], false).Should().BeFalse();

	/// <summary>ADR 0040: an actor with none of the qualifying roles still sees costs for a node they own or control via an ancestor.</summary>
	[Fact]
	public void An_owner_with_no_qualifying_role_may_view_costs_for_their_own_node() =>
		CostAccessPolicy.CanView([EmployeeRole.Worker], true).Should().BeTrue();

	/// <summary>ADR 0040: ownership does not widen visibility elsewhere -- a non-owner without a qualifying role is still denied.</summary>
	[Fact]
	public void A_non_owner_with_no_qualifying_role_may_not_view_costs() => CostAccessPolicy.CanView([EmployeeRole.Worker], false).Should().BeFalse();

	[Fact]
	public void A_null_role_collection_is_rejected()
	{
		var act = () => CostAccessPolicy.CanView(null!, false);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void An_owner_may_see_their_own_leafs_individual_cost() =>
		CostAccessPolicy.CanViewNodeCost([EmployeeRole.Worker], false, Actor, Actor).Should().BeTrue();

	[Fact]
	public void An_owner_may_not_see_another_workers_leaf_cost() =>
		CostAccessPolicy.CanViewNodeCost([EmployeeRole.Worker], false, OtherWorker, Actor).Should().BeFalse();

	[Fact]
	public void An_unassigned_leafs_cost_is_visible_since_no_workers_rate_is_inferable() =>
		CostAccessPolicy.CanViewNodeCost([EmployeeRole.Worker], false, null, Actor).Should().BeTrue();

	[Fact]
	public void A_branch_total_stays_visible_even_when_owned_by_another_worker() =>
		CostAccessPolicy.CanViewNodeCost([EmployeeRole.Worker], true, OtherWorker, Actor).Should().BeTrue();

	[Fact]
	public void A_cost_viewer_sees_every_individual_leaf_cost() =>
		CostAccessPolicy.CanViewNodeCost([EmployeeRole.CostViewer], false, OtherWorker, Actor).Should().BeTrue();

	[Fact]
	public void An_administrator_sees_every_individual_leaf_cost() =>
		CostAccessPolicy.CanViewNodeCost([EmployeeRole.Administrator], false, OtherWorker, Actor).Should().BeTrue();

	[Fact]
	public void A_null_role_collection_is_rejected_by_CanViewNodeCost()
	{
		var act = () => CostAccessPolicy.CanViewNodeCost(null!, false, OtherWorker, Actor);

		act.Should().Throw<ArgumentNullException>();
	}
}
