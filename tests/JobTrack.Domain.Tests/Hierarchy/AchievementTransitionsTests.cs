namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

public sealed class AchievementTransitionsTests
{
	[Theory]
	[InlineData(Achievement.Waiting, Achievement.InProgress)]
	[InlineData(Achievement.Waiting, Achievement.Cancelled)]
	[InlineData(Achievement.Waiting, Achievement.Unsuccessful)]
	[InlineData(Achievement.InProgress, Achievement.Success)]
	[InlineData(Achievement.InProgress, Achievement.Cancelled)]
	[InlineData(Achievement.InProgress, Achievement.Unsuccessful)]
	[InlineData(Achievement.Success, Achievement.Waiting)]
	[InlineData(Achievement.Cancelled, Achievement.Waiting)]
	[InlineData(Achievement.Unsuccessful, Achievement.Waiting)]
	public void Permitted_transitions_are_accepted(Achievement from, Achievement to) =>
		AchievementTransitions.IsPermitted(from, to).Should().BeTrue();

	[Theory]
	[InlineData(Achievement.Waiting, Achievement.Success)]
	[InlineData(Achievement.Waiting, Achievement.Waiting)]
	[InlineData(Achievement.InProgress, Achievement.Waiting)]
	[InlineData(Achievement.InProgress, Achievement.InProgress)]
	[InlineData(Achievement.Success, Achievement.InProgress)]
	[InlineData(Achievement.Success, Achievement.Cancelled)]
	[InlineData(Achievement.Success, Achievement.Success)]
	[InlineData(Achievement.Cancelled, Achievement.Unsuccessful)]
	[InlineData(Achievement.Unsuccessful, Achievement.Success)]
	public void Every_other_transition_is_rejected(Achievement from, Achievement to) =>
		AchievementTransitions.IsPermitted(from, to).Should().BeFalse();

	[Theory]
	[InlineData(Achievement.Waiting, false)]
	[InlineData(Achievement.InProgress, false)]
	[InlineData(Achievement.Success, true)]
	[InlineData(Achievement.Cancelled, true)]
	[InlineData(Achievement.Unsuccessful, true)]
	public void IsCompletedState_identifies_the_three_terminal_states(Achievement achievement, bool expected) =>
		AchievementTransitions.IsCompletedState(achievement).Should().Be(expected);

	[Theory]
	[InlineData(Achievement.Success, Achievement.Waiting, true)]
	[InlineData(Achievement.Cancelled, Achievement.Waiting, true)]
	[InlineData(Achievement.Unsuccessful, Achievement.Waiting, true)]
	[InlineData(Achievement.Waiting, Achievement.InProgress, false)]
	[InlineData(Achievement.InProgress, Achievement.Success, false)]
	public void IsReopening_identifies_only_terminal_to_waiting_transitions(Achievement from, Achievement to, bool expected) =>
		AchievementTransitions.IsReopening(from, to).Should().Be(expected);
}
