namespace JobTrack.Domain.Tests.Schedules;

using Abstractions;
using AwesomeAssertions;
using Domain.Intervals;
using Domain.Schedules;
using NodaTime;

public sealed class ScheduleExpanderTests
{
	private static readonly DateTimeZone Utc = DateTimeZone.Utc;
	private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];

	private static WorkInterval Bounds(LocalDate from, LocalDate to, DateTimeZone zone) =>
		new(from.AtMidnight().InZoneStrictly(zone).ToInstant(), to.AtMidnight().InZoneStrictly(zone).ToInstant());

	[Fact]
	public void A_weekly_interval_expands_once_per_matching_day_within_bounds()
	{
		EquatableArray<WeeklyInterval> weeklyIntervals = [
			new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0)),
		];
		var schedule = new ScheduleVersion(Utc, new(2024, 1, 1), null, weeklyIntervals);
		var bounds = Bounds(new(2024, 1, 1), new(2024, 1, 15), Utc);

		var expanded = ScheduleExpander.Expand(schedule, bounds);

		expanded.Should().BeEquivalentTo([
			new(Instant.FromUtc(2024, 1, 1, 9, 0), Instant.FromUtc(2024, 1, 1, 17, 0)),
			new WorkInterval(Instant.FromUtc(2024, 1, 8, 9, 0), Instant.FromUtc(2024, 1, 8, 17, 0)),
		]);
	}

	[Fact]
	public void A_day_outside_the_effective_range_produces_no_occurrence()
	{
		EquatableArray<WeeklyInterval> weeklyIntervals = [
			new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0)),
		];
		// Effective only from 2024-01-09, so the one Monday inside bounds (2024-01-08) is excluded.
		var schedule = new ScheduleVersion(Utc, new(2024, 1, 9), null, weeklyIntervals);
		var bounds = Bounds(new(2024, 1, 1), new(2024, 1, 9), Utc);

		var expanded = ScheduleExpander.Expand(schedule, bounds);

		expanded.Should().BeEmpty();
	}

	[Fact]
	public void An_interval_that_crosses_midnight_spans_into_the_following_day()
	{
		EquatableArray<WeeklyInterval> weeklyIntervals = [
			new(IsoDayOfWeek.Saturday, new(22, 0), new(2, 0)),
		];
		var schedule = new ScheduleVersion(Utc, new(2024, 1, 1), null, weeklyIntervals);
		var bounds = Bounds(new(2024, 1, 1), new(2024, 1, 8), Utc);

		var expanded = ScheduleExpander.Expand(schedule, bounds);

		expanded.Should().BeEquivalentTo([
			new WorkInterval(Instant.FromUtc(2024, 1, 6, 22, 0), Instant.FromUtc(2024, 1, 7, 2, 0)),
		]);
	}

	[Fact]
	public void Occurrences_outside_the_requested_bounds_are_clipped()
	{
		EquatableArray<WeeklyInterval> weeklyIntervals = [
			new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0)),
		];
		var schedule = new ScheduleVersion(Utc, new(2024, 1, 1), null, weeklyIntervals);
		var bounds = new WorkInterval(Instant.FromUtc(2024, 1, 1, 12, 0), Instant.FromUtc(2024, 1, 1, 17, 0));

		var expanded = ScheduleExpander.Expand(schedule, bounds);

		expanded.Should().BeEquivalentTo([new WorkInterval(Instant.FromUtc(2024, 1, 1, 12, 0), Instant.FromUtc(2024, 1, 1, 17, 0))]);
	}

	[Fact]
	public void A_spring_forward_gap_shortens_the_occurrence_by_the_gap_length()
	{
		// 2024-03-10 is a Sunday; America/New_York springs forward at 02:00 local to 03:00 local.
		EquatableArray<WeeklyInterval> weeklyIntervals = [
			new(IsoDayOfWeek.Sunday, new(2, 30), new(4, 0)),
		];
		var schedule = new ScheduleVersion(NewYork, new(2024, 1, 1), null, weeklyIntervals);
		var bounds = Bounds(new(2024, 3, 9), new(2024, 3, 11), NewYork);

		var expanded = ScheduleExpander.Expand(schedule, bounds);

		expanded.Should().ContainSingle();
		var occurrence = expanded[0];
		occurrence.Start.InZone(NewYork).LocalDateTime.Should().Be(new(2024, 3, 10, 3, 30, 0));
		occurrence.End.InZone(NewYork).LocalDateTime.Should().Be(new(2024, 3, 10, 4, 0, 0));
	}

	[Fact]
	public void An_autumn_fold_lengthens_the_occurrence_by_using_the_earlier_start()
	{
		// 2024-11-03 is a Sunday; America/New_York falls back at 02:00 local to 01:00 local, so
		// 01:00-02:00 local occurs twice.
		EquatableArray<WeeklyInterval> weeklyIntervals = [
			new(IsoDayOfWeek.Sunday, new(1, 30), new(2, 30)),
		];
		var schedule = new ScheduleVersion(NewYork, new(2024, 1, 1), null, weeklyIntervals);
		var bounds = Bounds(new(2024, 11, 2), new(2024, 11, 4), NewYork);
		var mapping = NewYork.MapLocal(new(2024, 11, 3, 1, 30, 0));
		var expectedStart = new OffsetDateTime(new(2024, 11, 3, 1, 30, 0), mapping.EarlyInterval.WallOffset).ToInstant();

		var expanded = ScheduleExpander.Expand(schedule, bounds);

		expanded.Should().ContainSingle();
		expanded[0].Start.Should().Be(expectedStart);
		expanded[0].Duration.Should().Be(Duration.FromHours(2));
	}
}
