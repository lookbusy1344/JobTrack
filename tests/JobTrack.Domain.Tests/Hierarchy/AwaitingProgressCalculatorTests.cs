namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;
using NodaTime;

public sealed class AwaitingProgressCalculatorTests
{
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId BranchId = new(2);
	private static readonly JobNodeId LeafAId = new(3);
	private static readonly JobNodeId LeafBId = new(4);
	private static readonly JobNodeId LeafCId = new(5);
	private static readonly JobNodeId RequiredId = new(6);
	private static readonly AppUserId Alice = new(100);
	private static readonly AppUserId Bob = new(200);

	private static HierarchyNode Node(JobNodeId id, JobNodeId? parentId, EquatableArray<JobNodeId> children, Achievement? achievement) =>
		new(id, parentId, children, achievement);

	/// <summary>
	///     A standalone leaf parented directly under the shared root-like <see cref="BranchId" />
	///     stub that <see cref="NodesWithParent" /> adds — readiness resolution walks the full ancestor
	///     chain for every candidate, not only when <c>subtreeRootId</c> is supplied, so every leaf's
	///     parent must actually exist in the node set.
	/// </summary>
	private static HierarchyNode Leaf(JobNodeId id, Achievement? achievement) => Node(id, BranchId, [], achievement);

	/// <summary>
	///     Builds a node dictionary from standalone <see cref="Leaf" /> nodes plus the shared
	///     root-like <see cref="BranchId" /> stub they are parented under.
	/// </summary>
	private static Dictionary<JobNodeId, HierarchyNode> NodesWithParent(params ReadOnlySpan<HierarchyNode> leaves)
	{
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [BranchId] = Node(BranchId, null, [], null) };
		foreach (var leaf in leaves) {
			nodes[leaf.Id] = leaf;
		}

		return nodes;
	}

	private static AwaitingProgressNodeFacts Facts(
		JobNodeId id, string description = "Job", AppUserId? owner = null, Priority priority = Priority.Medium,
		Instant? neededStart = null, Instant? neededFinish = null, Instant? archivedAt = null) =>
		new(id, description, owner ?? Alice, priority, neededStart, neededFinish, archivedAt);

	[Fact]
	public void A_leaf_awaiting_or_in_progress_is_included()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, Achievement.Waiting), Leaf(LeafBId, Achievement.InProgress));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> { [LeafAId] = Facts(LeafAId), [LeafBId] = Facts(LeafBId) };

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Select(e => e.Id).Should().BeEquivalentTo([LeafAId, LeafBId]);
	}

	[Theory]
	[InlineData(Achievement.Success)]
	[InlineData(Achievement.Cancelled)]
	[InlineData(Achievement.Unsuccessful)]
	public void A_leaf_in_a_terminal_state_is_excluded(Achievement achievement)
	{
		var nodes = NodesWithParent(Leaf(LeafAId, achievement));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> { [LeafAId] = Facts(LeafAId) };

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Should().BeEmpty();
	}

	[Fact]
	public void A_leaf_with_no_leaf_work_attached_is_included_with_a_null_achievement()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, null));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> { [LeafAId] = Facts(LeafAId) };

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Should().ContainSingle().Which.Achievement.Should().BeNull();
	}

	[Fact]
	public void A_branch_or_the_root_is_never_included_even_when_childless_or_when_waiting_like_leaf_facts_exist()
	{
		var nodes = new Dictionary<JobNodeId, HierarchyNode> {
			[RootId] = Node(RootId, null, [BranchId], null),
			[BranchId] = Node(BranchId, RootId, [LeafAId], null),
			[LeafAId] = Node(LeafAId, BranchId, [], Achievement.Waiting),
		};
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[RootId] = Facts(RootId),
			[BranchId] = Facts(BranchId),
			[LeafAId] = Facts(LeafAId),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Select(e => e.Id).Should().BeEquivalentTo([LeafAId]);
	}

	[Fact]
	public void A_childless_root_is_never_included_even_though_it_has_no_leaf_work()
	{
		var nodes = new Dictionary<JobNodeId, HierarchyNode> { [RootId] = Node(RootId, null, [], null) };
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> { [RootId] = Facts(RootId) };

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Should().BeEmpty();
	}

	[Fact]
	public void A_leaf_blocked_by_an_unsatisfied_prerequisite_stays_on_the_list_marked_not_ready()
	{
		var nodes = NodesWithParent(Leaf(RequiredId, Achievement.InProgress), Leaf(LeafAId, Achievement.Waiting));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> { [RequiredId] = Facts(RequiredId), [LeafAId] = Facts(LeafAId) };
		var edges = new[] { new PrerequisiteEdge(RequiredId, LeafAId) };

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, edges, OwnershipFilter.All, null);

		result.Single(e => e.Id == LeafAId).IsReady.Should().BeFalse();
	}

	[Fact]
	public void A_leaf_whose_prerequisite_has_succeeded_is_included_and_ready()
	{
		var nodes = NodesWithParent(Leaf(RequiredId, Achievement.Success), Leaf(LeafAId, Achievement.Waiting));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> { [RequiredId] = Facts(RequiredId), [LeafAId] = Facts(LeafAId) };
		var edges = new[] { new PrerequisiteEdge(RequiredId, LeafAId) };

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, edges, OwnershipFilter.All, null);

		result.Single(e => e.Id == LeafAId).IsReady.Should().BeTrue();
	}

	[Fact]
	public void An_archived_leaf_is_excluded()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, Achievement.Waiting));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[LeafAId] = Facts(LeafAId, archivedAt: Instant.FromUtc(2026, 1, 1, 0, 0)),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Should().BeEmpty();
	}

	[Fact]
	public void An_owner_filter_restricts_to_the_matching_owner()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, Achievement.Waiting), Leaf(LeafBId, Achievement.Waiting));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[LeafAId] = Facts(LeafAId, owner: Alice),
			[LeafBId] = Facts(LeafBId, owner: Bob),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.OwnedBy(Alice), null);

		result.Select(e => e.Id).Should().BeEquivalentTo([LeafAId]);
	}

	[Fact]
	public void An_unassigned_filter_restricts_to_leaves_with_no_owner()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, Achievement.Waiting), Leaf(LeafBId, Achievement.Waiting));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[LeafAId] = new(LeafAId, "Job", null, Priority.Medium, null, null, null),
			[LeafBId] = Facts(LeafBId, owner: Bob),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(
			nodes, facts, [], OwnershipFilter.Unassigned, null);

		result.Select(e => e.Id).Should().BeEquivalentTo([LeafAId]);
	}

	[Fact]
	public void An_unassigned_leaf_is_returned_with_a_null_owner_and_does_not_throw()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, Achievement.Waiting));
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[LeafAId] = new(LeafAId, "Job", null, Priority.Medium, null, null, null),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(
			nodes, facts, [], OwnershipFilter.All, null);

		result.Single().OwnerUserId.Should().BeNull();
	}

	[Fact]
	public void A_subtree_filter_restricts_to_descendants_of_the_scope_root()
	{
		var nodes = new Dictionary<JobNodeId, HierarchyNode> {
			[RootId] = Node(RootId, null, [BranchId, LeafCId], null),
			[BranchId] = Node(BranchId, RootId, [LeafAId, LeafBId], null),
			[LeafAId] = Node(LeafAId, BranchId, [], Achievement.Waiting),
			[LeafBId] = Node(LeafBId, BranchId, [], Achievement.Waiting),
			[LeafCId] = Node(LeafCId, RootId, [], Achievement.Waiting),
		};
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[RootId] = Facts(RootId),
			[BranchId] = Facts(BranchId),
			[LeafAId] = Facts(LeafAId),
			[LeafBId] = Facts(LeafBId),
			[LeafCId] = Facts(LeafCId),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(
			nodes, facts, [], OwnershipFilter.All, BranchId);

		result.Select(e => e.Id).Should().BeEquivalentTo([LeafAId, LeafBId]);
	}

	[Fact]
	public void Results_are_ordered_by_descending_priority_then_ascending_deadline_with_nulls_last()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, Achievement.Waiting), Leaf(LeafBId, Achievement.Waiting), Leaf(LeafCId, Achievement.Waiting));
		var soon = Instant.FromUtc(2026, 1, 1, 0, 0);
		var later = Instant.FromUtc(2026, 6, 1, 0, 0);
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[LeafAId] = Facts(LeafAId, priority: Priority.High, neededFinish: later),
			[LeafBId] = Facts(LeafBId, priority: Priority.High, neededFinish: soon),
			[LeafCId] = Facts(LeafCId, priority: Priority.Urgent, neededFinish: null),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Select(e => e.Id).Should().ContainInOrder(LeafCId, LeafBId, LeafAId);
	}

	[Fact]
	public void NeededStart_is_used_as_a_fallback_deadline_when_NeededFinish_is_absent()
	{
		var nodes = NodesWithParent(Leaf(LeafAId, Achievement.Waiting), Leaf(LeafBId, Achievement.Waiting));
		var soon = Instant.FromUtc(2026, 1, 1, 0, 0);
		var later = Instant.FromUtc(2026, 6, 1, 0, 0);
		var facts = new Dictionary<JobNodeId, AwaitingProgressNodeFacts> {
			[LeafAId] = Facts(LeafAId, neededStart: later),
			[LeafBId] = Facts(LeafBId, neededStart: soon),
		};

		var result = AwaitingProgressCalculator.GetAwaitingProgress(nodes, facts, [], OwnershipFilter.All, null);

		result.Select(e => e.Id).Should().ContainInOrder(LeafBId, LeafAId);
	}
}
