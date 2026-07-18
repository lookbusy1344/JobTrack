namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

public sealed class AchievementCalculatorTests
{
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId LeftId = new(2);
	private static readonly JobNodeId RightId = new(3);

	private static HierarchyNode Leaf(JobNodeId id, JobNodeId parentId, Achievement? achievement) =>
		new(id, parentId, [], achievement);

	[Fact]
	public void A_leaf_achieved_to_success_is_achieved()
	{
		var leaf = Leaf(LeftId, RootId, Achievement.Success);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [LeftId] = leaf };

		AchievementCalculator.IsAchieved(LeftId, nodes).Should().BeTrue();
	}

	[Theory]
	[InlineData(Achievement.Waiting)]
	[InlineData(Achievement.InProgress)]
	[InlineData(Achievement.Cancelled)]
	[InlineData(Achievement.Unsuccessful)]
	public void A_leaf_not_achieved_to_success_is_not_achieved(Achievement achievement)
	{
		var leaf = Leaf(LeftId, RootId, achievement);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [LeftId] = leaf };

		AchievementCalculator.IsAchieved(LeftId, nodes).Should().BeFalse();
	}

	[Fact]
	public void A_leaf_without_leaf_work_is_not_achieved()
	{
		var leaf = Leaf(LeftId, RootId, null);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [LeftId] = leaf };

		AchievementCalculator.IsAchieved(LeftId, nodes).Should().BeFalse();
	}

	[Fact]
	public void A_branch_is_achieved_when_every_child_is_achieved()
	{
		var left = Leaf(LeftId, RootId, Achievement.Success);
		var right = Leaf(RightId, RootId, Achievement.Success);
		var root = new HierarchyNode(RootId, null, [LeftId, RightId], null);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RootId] = root, [LeftId] = left, [RightId] = right };

		AchievementCalculator.IsAchieved(RootId, nodes).Should().BeTrue();
	}

	[Fact]
	public void A_branch_is_not_achieved_when_any_child_is_not_achieved()
	{
		var left = Leaf(LeftId, RootId, Achievement.Success);
		var right = Leaf(RightId, RootId, Achievement.InProgress);
		var root = new HierarchyNode(RootId, null, [LeftId, RightId], null);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RootId] = root, [LeftId] = left, [RightId] = right };

		AchievementCalculator.IsAchieved(RootId, nodes).Should().BeFalse();
	}

	[Fact]
	public void The_root_follows_the_same_recursive_rule_through_a_deeper_subtree()
	{
		var grandchildId = new JobNodeId(4);
		var grandchild = Leaf(grandchildId, LeftId, Achievement.InProgress);
		var left = new HierarchyNode(LeftId, RootId, [grandchildId], null);
		var right = Leaf(RightId, RootId, Achievement.Success);
		var root = new HierarchyNode(RootId, null, [LeftId, RightId], null);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> {
			[RootId] = root,
			[LeftId] = left,
			[RightId] = right,
			[grandchildId] = grandchild,
		};

		AchievementCalculator.IsAchieved(RootId, nodes).Should().BeFalse();
	}

	[Fact]
	public void A_deep_linear_chain_is_evaluated_without_stack_overflow()
	{
		const int depth = 50_000;
		var nodes = new Dictionary<JobNodeId, HierarchyNode>(depth + 1);

		var leafId = new JobNodeId(depth);
		nodes[leafId] = Leaf(leafId, new(depth - 1), Achievement.Success);

		for (var level = depth - 1; level >= 0; level--) {
			var id = new JobNodeId(level);
			var parentId = level == 0 ? (JobNodeId?)null : new JobNodeId(level - 1);
			nodes[id] = new(id, parentId, [new(level + 1)], null);
		}

		AchievementCalculator.IsAchieved(new(0), nodes).Should().BeTrue();
	}

	[Fact]
	public void IsAchieved_throws_an_invariant_violation_when_a_referenced_node_is_missing()
	{
		var act = () => AchievementCalculator.IsAchieved(new(1), new Dictionary<JobNodeId, HierarchyNode>());

		act.Should().Throw<InvariantViolationException>()
			.Which.ConstraintId.Should().Be("hierarchy.missing-node");
	}
}
