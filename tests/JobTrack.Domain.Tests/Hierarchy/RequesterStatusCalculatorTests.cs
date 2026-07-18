namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

public sealed class RequesterStatusCalculatorTests
{
	private static RequesterSubtreeLeafState Leaf(Achievement? achievement) => new() { LeafAchievement = achievement };

	[Fact]
	public void An_unacknowledged_request_with_no_actionable_work_is_submitted() =>
		RequesterStatusCalculator.Derive(false, []).Should().Be(RequesterStatus.Submitted);

	[Fact]
	public void An_acknowledged_request_with_no_actionable_work_is_accepted() =>
		RequesterStatusCalculator.Derive(true, []).Should().Be(RequesterStatus.Accepted);

	[Fact]
	public void An_acknowledged_request_with_a_leaf_still_lacking_leaf_work_is_accepted() =>
		RequesterStatusCalculator.Derive(true, [Leaf(null)]).Should().Be(RequesterStatus.Accepted);

	[Fact]
	public void Any_leaf_waiting_makes_the_request_waiting_even_if_acknowledged() =>
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Waiting)]).Should().Be(RequesterStatus.Waiting);

	[Fact]
	public void Any_leaf_in_progress_makes_the_request_in_progress()
	{
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.InProgress), Leaf(Achievement.Waiting)])
			.Should().Be(RequesterStatus.InProgress);
	}

	[Fact]
	public void A_single_leaf_succeeded_request_is_completed() =>
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Success)]).Should().Be(RequesterStatus.Completed);

	[Fact]
	public void Every_leaf_must_succeed_for_the_request_to_be_completed()
	{
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Success), Leaf(Achievement.InProgress)])
			.Should().Be(RequesterStatus.InProgress);
	}

	[Theory]
	[InlineData(Achievement.Cancelled)]
	[InlineData(Achievement.Unsuccessful)]
	public void A_single_leaf_terminal_negative_request_is_cancelled(Achievement achievement) =>
		RequesterStatusCalculator.Derive(true, [Leaf(achievement)]).Should().Be(RequesterStatus.Cancelled);

	[Fact]
	public void Mixed_terminal_negative_leaves_are_still_cancelled()
	{
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Cancelled), Leaf(Achievement.Unsuccessful)])
			.Should().Be(RequesterStatus.Cancelled);
	}

	[Fact]
	public void A_terminal_negative_leaf_alongside_a_leaf_still_lacking_leaf_work_is_not_cancelled_yet()
	{
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Cancelled), Leaf(null)])
			.Should().Be(RequesterStatus.Accepted);
	}

	[Fact]
	public void A_terminal_negative_leaf_alongside_a_waiting_leaf_is_waiting_not_cancelled()
	{
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Cancelled), Leaf(Achievement.Waiting)])
			.Should().Be(RequesterStatus.Waiting);
	}

	[Fact]
	public void A_succeeded_leaf_alongside_a_still_pending_leaf_is_not_completed()
	{
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Success), Leaf(null)])
			.Should().Be(RequesterStatus.Accepted);
	}

	[Fact]
	public void Completed_takes_precedence_over_cancelled_when_every_leaf_succeeded()
	{
		RequesterStatusCalculator.Derive(true, [Leaf(Achievement.Success), Leaf(Achievement.Success)])
			.Should().Be(RequesterStatus.Completed);
	}
}
