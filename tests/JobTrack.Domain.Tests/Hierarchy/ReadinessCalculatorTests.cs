namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

public sealed class ReadinessCalculatorTests
{
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId BranchId = new(2);
	private static readonly JobNodeId LeafId = new(3);
	private static readonly JobNodeId RequiredId = new(4);

	private static HierarchyNode Leaf(JobNodeId id, JobNodeId? parentId, Achievement? achievement) =>
		new(id, parentId, [], achievement);

	[Fact]
	public void A_leaf_with_no_prerequisites_is_ready()
	{
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [LeafId] = Leaf(LeafId, null, Achievement.Waiting) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, []);

		result.IsReady.Should().BeTrue();
		result.Blockers.Should().BeEmpty();
	}

	[Fact]
	public void A_leaf_is_ready_when_its_own_prerequisite_has_succeeded()
	{
		var required = Leaf(RequiredId, null, Achievement.Success);
		var leaf = Leaf(LeafId, null, Achievement.Waiting);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RequiredId] = required, [LeafId] = leaf };
		var edges = new[] { new PrerequisiteEdge(RequiredId, LeafId) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, edges);

		result.IsReady.Should().BeTrue();
		result.Blockers.Should().BeEmpty();
	}

	[Fact]
	public void A_leaf_is_not_ready_when_its_own_prerequisite_has_not_succeeded()
	{
		var required = Leaf(RequiredId, null, Achievement.InProgress);
		var leaf = Leaf(LeafId, null, Achievement.Waiting);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RequiredId] = required, [LeafId] = leaf };
		var edges = new[] { new PrerequisiteEdge(RequiredId, LeafId) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, edges);

		result.IsReady.Should().BeFalse();
		result.Blockers.Should().BeEquivalentTo([new UnsatisfiedPrerequisite(RequiredId, LeafId)]);
	}

	[Fact]
	public void A_leaf_inherits_an_unsatisfied_prerequisite_declared_on_an_ancestor()
	{
		var required = Leaf(RequiredId, null, Achievement.Waiting);
		var branch = new HierarchyNode(BranchId, RootId, [LeafId], null);
		var root = new HierarchyNode(RootId, null, [BranchId], null);
		var leaf = Leaf(LeafId, BranchId, Achievement.Waiting);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> {
			[RequiredId] = required,
			[RootId] = root,
			[BranchId] = branch,
			[LeafId] = leaf,
		};
		var edges = new[] { new PrerequisiteEdge(RequiredId, BranchId) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, edges);

		result.IsReady.Should().BeFalse();
		result.Blockers.Should().BeEquivalentTo([new UnsatisfiedPrerequisite(RequiredId, BranchId)]);
	}

	[Fact]
	public void A_leaf_is_ready_when_an_inherited_prerequisite_has_succeeded()
	{
		var required = Leaf(RequiredId, null, Achievement.Success);
		var branch = new HierarchyNode(BranchId, null, [LeafId], null);
		var leaf = Leaf(LeafId, BranchId, Achievement.Waiting);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RequiredId] = required, [BranchId] = branch, [LeafId] = leaf };
		var edges = new[] { new PrerequisiteEdge(RequiredId, BranchId) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, edges);

		result.IsReady.Should().BeTrue();
		result.Blockers.Should().BeEmpty();
	}

	[Fact]
	public void Blockers_from_the_leaf_and_every_ancestor_are_all_reported()
	{
		var ownRequired = new JobNodeId(5);
		var ancestorRequired = new JobNodeId(6);
		var own = Leaf(ownRequired, null, Achievement.Waiting);
		var ancestor = Leaf(ancestorRequired, null, Achievement.Cancelled);
		var branch = new HierarchyNode(BranchId, null, [LeafId], null);
		var leaf = Leaf(LeafId, BranchId, Achievement.Waiting);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> {
			[ownRequired] = own,
			[ancestorRequired] = ancestor,
			[BranchId] = branch,
			[LeafId] = leaf,
		};
		var edges = new[] { new PrerequisiteEdge(ownRequired, LeafId), new PrerequisiteEdge(ancestorRequired, BranchId) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, edges);

		result.IsReady.Should().BeFalse();
		result.Blockers.Should().BeEquivalentTo([
			new(ownRequired, LeafId),
			new UnsatisfiedPrerequisite(ancestorRequired, BranchId),
		]);
	}

	[Fact]
	public void A_prerequisite_on_a_branch_required_job_is_satisfied_only_when_every_descendant_leaf_succeeds()
	{
		var grandchildId = new JobNodeId(7);
		var grandchild = Leaf(grandchildId, RequiredId, Achievement.InProgress);
		var requiredBranch = new HierarchyNode(RequiredId, null, [grandchildId], null);
		var leaf = Leaf(LeafId, null, Achievement.Waiting);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RequiredId] = requiredBranch, [grandchildId] = grandchild, [LeafId] = leaf };
		var edges = new[] { new PrerequisiteEdge(RequiredId, LeafId) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, edges);

		result.IsReady.Should().BeFalse();
		result.Blockers.Should().BeEquivalentTo([new UnsatisfiedPrerequisite(RequiredId, LeafId)]);
	}

	[Fact]
	public void A_prerequisite_declared_on_an_unrelated_node_does_not_block_readiness()
	{
		var unrelatedDependentId = new JobNodeId(8);
		var required = Leaf(RequiredId, null, Achievement.Waiting);
		var leaf = Leaf(LeafId, null, Achievement.Waiting);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RequiredId] = required, [LeafId] = leaf };
		var edges = new[] { new PrerequisiteEdge(RequiredId, unrelatedDependentId) };

		var result = ReadinessCalculator.IsReady(LeafId, nodes, edges);

		result.IsReady.Should().BeTrue();
		result.Blockers.Should().BeEmpty();
	}
}
