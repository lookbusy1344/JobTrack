namespace JobTrack.Domain.Tests.Schedules;

using AwesomeAssertions;
using Domain.Schedules;
using NodaTime;

public sealed class ScheduleVersionTests
{
	private static readonly DateTimeZone Utc = DateTimeZone.Utc;

	[Fact]
	public void An_effective_end_at_or_before_the_effective_start_throws()
	{
		var start = new LocalDate(2024, 1, 1);

		var act = () => new ScheduleVersion(Utc, start, start, []);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void A_date_before_the_effective_start_is_not_effective()
	{
		var version = new ScheduleVersion(Utc, new(2024, 2, 1), null, []);

		version.IsEffectiveOn(new(2024, 1, 31)).Should().BeFalse();
	}

	[Fact]
	public void A_date_on_or_after_the_effective_start_and_before_the_exclusive_end_is_effective()
	{
		var version = new ScheduleVersion(Utc, new(2024, 2, 1), new LocalDate(2024, 3, 1), []);

		version.IsEffectiveOn(new(2024, 2, 1)).Should().BeTrue();
		version.IsEffectiveOn(new(2024, 2, 29)).Should().BeTrue();
		version.IsEffectiveOn(new(2024, 3, 1)).Should().BeFalse();
	}

	[Fact]
	public void With_no_effective_end_every_date_from_the_start_onward_is_effective()
	{
		var version = new ScheduleVersion(Utc, new(2024, 2, 1), null, []);

		version.IsEffectiveOn(new(2099, 1, 1)).Should().BeTrue();
	}
}
