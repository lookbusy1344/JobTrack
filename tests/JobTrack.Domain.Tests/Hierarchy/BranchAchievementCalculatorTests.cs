namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

public sealed class BranchAchievementCalculatorTests
{
	private static RequesterSubtreeLeafState Leaf(Achievement? achievement) => new() {
		LeafAchievement = achievement,
	};

	[Fact]
	public void A_subtree_whose_every_childless_node_succeeded_is_success() =>
		BranchAchievementCalculator.Derive([Leaf(Achievement.Success), Leaf(Achievement.Success)])
								   .Should().Be(BranchAchievement.Success);

	[Fact]
	public void A_single_succeeded_childless_node_is_success() =>
		BranchAchievementCalculator.Derive([Leaf(Achievement.Success)]).Should().Be(BranchAchievement.Success);

	[Theory]
	[InlineData(Achievement.Waiting)]
	[InlineData(Achievement.InProgress)]
	[InlineData(Achievement.Cancelled)]
	[InlineData(Achievement.Unsuccessful)]
	[InlineData(Achievement.None)]
	public void Any_childless_node_short_of_success_leaves_the_subtree_unfinished(Achievement achievement) =>
		BranchAchievementCalculator.Derive([Leaf(Achievement.Success), Leaf(achievement)])
								   .Should().Be(BranchAchievement.Unfinished);

	/// <summary>
	///     Mirrors <c>node_succeeded</c>'s explicit case (schema version 0013): a childless node with no
	///     <c>leaf_work</c> row never succeeds, so neither does any subtree containing one.
	/// </summary>
	[Fact]
	public void A_childless_node_without_leaf_work_leaves_the_subtree_unfinished() =>
		BranchAchievementCalculator.Derive([Leaf(null)]).Should().Be(BranchAchievement.Unfinished);

	[Fact]
	public void An_empty_leaf_set_is_unfinished() =>
		BranchAchievementCalculator.Derive([]).Should().Be(BranchAchievement.Unfinished);

	[Fact]
	public void A_null_leaf_collection_is_rejected()
	{
		var act = () => BranchAchievementCalculator.Derive(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
