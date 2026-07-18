namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

public sealed class NodeClassifierTests
{
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId ChildId = new(2);

	[Fact]
	public void A_node_with_no_parent_and_no_leaf_work_is_the_root()
	{
		var node = new HierarchyNode(RootId, null, [ChildId], null);

		NodeClassifier.Classify(node).Should().Be(NodeKind.Root);
	}

	[Fact]
	public void A_node_with_no_parent_that_owns_leaf_work_is_rejected()
	{
		var node = new HierarchyNode(RootId, null, [], Achievement.Waiting);

		var act = () => NodeClassifier.Classify(node);

		act.Should().Throw<InvariantViolationException>();
	}

	[Fact]
	public void A_node_with_children_and_no_leaf_work_is_a_branch()
	{
		var node = new HierarchyNode(ChildId, RootId, [new(3)], null);

		NodeClassifier.Classify(node).Should().Be(NodeKind.Branch);
	}

	[Fact]
	public void A_node_with_children_and_leaf_work_is_rejected()
	{
		var node = new HierarchyNode(ChildId, RootId, [new(3)], Achievement.Waiting);

		var act = () => NodeClassifier.Classify(node);

		act.Should().Throw<InvariantViolationException>();
	}

	[Fact]
	public void A_childless_node_with_no_leaf_work_is_a_leaf()
	{
		var node = new HierarchyNode(ChildId, RootId, [], null);

		NodeClassifier.Classify(node).Should().Be(NodeKind.Leaf);
	}

	[Fact]
	public void A_childless_node_with_leaf_work_is_a_leaf()
	{
		var node = new HierarchyNode(ChildId, RootId, [], Achievement.InProgress);

		NodeClassifier.Classify(node).Should().Be(NodeKind.Leaf);
	}

	[Fact]
	public void A_node_that_is_its_own_parent_is_rejected()
	{
		var node = new HierarchyNode(ChildId, ChildId, [], null);

		var act = () => NodeClassifier.Classify(node);

		act.Should().Throw<InvariantViolationException>();
	}
}
