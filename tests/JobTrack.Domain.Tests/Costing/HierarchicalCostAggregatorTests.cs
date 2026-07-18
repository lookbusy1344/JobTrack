namespace JobTrack.Domain.Tests.Costing;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using Domain.Hierarchy;

public sealed class HierarchicalCostAggregatorTests
{
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId LeftId = new(2);
	private static readonly JobNodeId RightId = new(3);

	private static HierarchyNode Leaf(JobNodeId id, JobNodeId parentId) => new(id, parentId, [], Achievement.InProgress);

	[Fact]
	public void A_leaf_with_a_recorded_cost_reports_that_cost()
	{
		var leaf = Leaf(LeftId, RootId);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [LeftId] = leaf };
		var leafCosts = new Dictionary<JobNodeId, Money> { [LeftId] = new(42m) };

		var costs = HierarchicalCostAggregator.Aggregate(LeftId, nodes, leafCosts);

		costs[LeftId].Should().Be(new Money(42m));
	}

	[Fact]
	public void A_leaf_without_sessions_costs_zero()
	{
		var leaf = Leaf(LeftId, RootId);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [LeftId] = leaf };

		var costs = HierarchicalCostAggregator.Aggregate(LeftId, nodes, new Dictionary<JobNodeId, Money>());

		costs[LeftId].Should().Be(new Money(0m));
	}

	[Fact]
	public void A_branch_costs_the_sum_of_its_children()
	{
		var left = Leaf(LeftId, RootId);
		var right = Leaf(RightId, RootId);
		var root = new HierarchyNode(RootId, null, [LeftId, RightId], null);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RootId] = root, [LeftId] = left, [RightId] = right };
		var leafCosts = new Dictionary<JobNodeId, Money> { [LeftId] = new(10m), [RightId] = new(15m) };

		var costs = HierarchicalCostAggregator.Aggregate(RootId, nodes, leafCosts);

		costs[RootId].Should().Be(new Money(25m));
	}

	[Fact]
	public void The_root_sums_every_descendant_leaf_through_a_deeper_subtree()
	{
		var grandchildId = new JobNodeId(4);
		var grandchild = Leaf(grandchildId, LeftId);
		var left = new HierarchyNode(LeftId, RootId, [grandchildId], null);
		var right = Leaf(RightId, RootId);
		var root = new HierarchyNode(RootId, null, [LeftId, RightId], null);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> {
			[RootId] = root,
			[LeftId] = left,
			[RightId] = right,
			[grandchildId] = grandchild,
		};
		var leafCosts = new Dictionary<JobNodeId, Money> { [grandchildId] = new(5m), [RightId] = new(7m) };

		var costs = HierarchicalCostAggregator.Aggregate(RootId, nodes, leafCosts);

		costs[RootId].Should().Be(new Money(12m));
		costs[LeftId].Should().Be(new Money(5m));
	}

	[Fact]
	public void A_deep_linear_chain_is_aggregated_without_stack_overflow()
	{
		const int depth = 50_000;
		var nodes = new Dictionary<JobNodeId, HierarchyNode>(depth + 1);

		var leafId = new JobNodeId(depth);
		nodes[leafId] = Leaf(leafId, new(depth - 1));

		for (var level = depth - 1; level >= 0; level--) {
			var id = new JobNodeId(level);
			var parentId = level == 0 ? (JobNodeId?)null : new JobNodeId(level - 1);
			nodes[id] = new(id, parentId, [new(level + 1)], null);
		}

		var leafCosts = new Dictionary<JobNodeId, Money> { [leafId] = new(1m) };

		var costs = HierarchicalCostAggregator.Aggregate(new(0), nodes, leafCosts);

		costs[new(0)].Should().Be(new Money(1m));
	}
}
