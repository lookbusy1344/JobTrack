namespace JobTrack.Persistence.Shared.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

/// <summary>
///     Pins <see cref="LeafAchievementTransition.TriggersAcknowledgement" /> — ADR 0058's trigger set,
///     restated in <c>JobTrack.Persistence.Shared</c> because that assembly's dependency boundary stops
///     at <c>JobTrack.Abstractions</c> — against the domain's own terminal-state predicate, so the two
///     cannot drift.
/// </summary>
public sealed class LeafAchievementTransitionTests
{
	public static TheoryData<Achievement> EveryAchievement
	{
		get
		{
			var data = new TheoryData<Achievement>();
			foreach (var achievement in Enum.GetValues<Achievement>()) {
				data.Add(achievement);
			}

			return data;
		}
	}

	[Theory]
	[MemberData(nameof(EveryAchievement))]
	public void Trigger_set_is_in_progress_plus_every_domain_terminal_state(Achievement achievement) =>
		LeafAchievementTransition.TriggersAcknowledgement(achievement)
			.Should().Be(achievement == Achievement.InProgress || AchievementTransitions.IsCompletedState(achievement));

	[Fact]
	public void An_unrecognized_achievement_is_rejected_rather_than_silently_untriggered()
	{
		var act = () => LeafAchievementTransition.TriggersAcknowledgement((Achievement)int.MaxValue);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}
}
