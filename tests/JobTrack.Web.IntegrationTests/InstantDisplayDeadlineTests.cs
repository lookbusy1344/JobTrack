namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using NodaTime;

/// <summary>
///     <see cref="InstantDisplay.FormatDeadline" />: the record-card deadline field's own rendering --
///     the full local date and time, followed by how much of it is left, coarsening from whole days to
///     whole hours to whole minutes as the deadline approaches.
/// </summary>
public sealed class InstantDisplayDeadlineTests
{
	private static readonly DateTimeZone London = DateTimeZoneProviders.Tzdb["Europe/London"];
	private static readonly Instant Now = Instant.FromUtc(2026, 8, 8, 12, 0);

	[Fact]
	public void A_deadline_days_away_reads_as_a_local_stamp_and_whole_days_left() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 10, 15, 0), London, Now, true)
			.Should().Be("10 Aug 2026 16:00 (2 days)");

	[Fact]
	public void Part_days_round_to_the_nearest_day() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 11, 11, 0), London, Now, true)
			.Should().Be("11 Aug 2026 12:00 (3 days)");

	[Fact]
	public void Under_two_days_left_reads_as_whole_hours_rounded_to_the_nearest_hour() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 10, 10, 30), London, Now, true)
			.Should().Be("10 Aug 2026 11:30 (47 hrs)");

	[Fact]
	public void Ninety_minutes_left_reads_as_minutes_under_the_two_hour_threshold() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 13, 30), London, Now, true)
			.Should().Be("8 Aug 2026 14:30 (90 mins)");

	[Fact]
	public void Just_under_two_hours_left_still_reads_as_minutes() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 13, 59), London, Now, true)
			.Should().Be("8 Aug 2026 14:59 (119 mins)");

	[Fact]
	public void Two_hours_left_reads_as_whole_hours() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 14, 0), London, Now, true)
			.Should().Be("8 Aug 2026 15:00 (2 hrs)");

	[Fact]
	public void Just_over_two_hours_left_rounds_up_rather_than_truncating() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 14, 1), London, Now, true)
			.Should().Be("8 Aug 2026 15:01 (2 hrs)");

	[Fact]
	public void Fifty_nine_minutes_left_reads_as_whole_minutes() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 12, 59), London, Now, true)
			.Should().Be("8 Aug 2026 13:59 (59 mins)");

	[Fact]
	public void Twenty_minutes_left_still_reports_the_minutes_rather_than_falling_silent() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 12, 20), London, Now, true)
			.Should().Be("8 Aug 2026 13:20 (20 mins)");

	[Fact]
	public void A_single_minute_left_reads_in_the_singular() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 12, 1), London, Now, true)
			.Should().Be("8 Aug 2026 13:01 (1 min)");

	[Fact]
	public void An_open_job_days_past_its_deadline_says_how_many_days_overdue() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 1, 9, 0), London, Now, true)
			.Should().Be("1 Aug 2026 10:00 (7 days overdue)");

	[Fact]
	public void Under_two_days_overdue_reads_as_whole_hours_rounded_to_the_nearest_hour() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 6, 14, 30), London, Now, true)
			.Should().Be("6 Aug 2026 15:30 (46 hrs overdue)");

	[Fact]
	public void Ninety_minutes_overdue_reads_as_minutes_under_the_two_hour_threshold() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 10, 30), London, Now, true)
			.Should().Be("8 Aug 2026 11:30 (90 mins overdue)");

	[Fact]
	public void Just_over_two_hours_overdue_rounds_up_rather_than_truncating() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 9, 59), London, Now, true)
			.Should().Be("8 Aug 2026 10:59 (2 hrs overdue)");

	[Fact]
	public void Forty_five_minutes_overdue_reads_as_whole_minutes() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 11, 15), London, Now, true)
			.Should().Be("8 Aug 2026 12:15 (45 mins overdue)");

	[Fact]
	public void Twenty_five_minutes_overdue_still_reports_the_minutes_rather_than_falling_silent() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 11, 35), London, Now, true)
			.Should().Be("8 Aug 2026 12:35 (25 mins overdue)");

	[Fact]
	public void A_single_minute_overdue_reads_in_the_singular() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 11, 59), London, Now, true)
			.Should().Be("8 Aug 2026 12:59 (1 min overdue)");

	[Fact]
	/// <summary>
	///     Overdue is a live alarm, so a job that has ended never carries it -- the same rule that keeps
	///     a closed job's missed deadline out of red.
	/// </summary>
	public void A_closed_job_past_its_deadline_shows_the_stamp_alone() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 1, 9, 0), London, Now, false)
			.Should().Be("1 Aug 2026 10:00");

	[Fact]
	/// <summary>Time still to run is a plain fact, reported whether the job has ended or not.</summary>
	public void A_closed_job_short_of_its_deadline_still_reports_the_time_left() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 10, 15, 0), London, Now, false)
			.Should().Be("10 Aug 2026 16:00 (2 days)");

	[Fact]
	public void Under_a_minute_left_reads_as_now() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 12, 0, 30), London, Now, true)
			.Should().Be("8 Aug 2026 13:00 (now)");

	[Fact]
	public void Under_a_minute_overdue_reads_as_now_not_overdue() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 11, 59, 30), London, Now, true)
			.Should().Be("8 Aug 2026 12:59 (now)");

	[Fact]
	public void Exactly_at_the_deadline_reads_as_now() =>
		InstantDisplay.FormatDeadline(Now, London, Now, true)
			.Should().Be("8 Aug 2026 13:00 (now)");

	[Fact]
	public void A_closed_job_within_a_minute_of_its_deadline_still_reads_as_now() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 11, 59, 30), London, Now, false)
			.Should().Be("8 Aug 2026 12:59 (now)");

	[Fact]
	public void Thirteen_days_left_still_reads_as_days_under_the_two_week_threshold() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 21, 12, 0), London, Now, true)
			.Should().Be("21 Aug 2026 13:00 (13 days)");

	[Fact]
	public void Fourteen_days_left_reads_as_whole_weeks() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 22, 12, 0), London, Now, true)
			.Should().Be("22 Aug 2026 13:00 (2 weeks)");

	[Fact]
	public void Six_weeks_left_reads_as_whole_weeks() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 9, 19, 12, 0), London, Now, true)
			.Should().Be("19 Sep 2026 13:00 (6 weeks)");

	[Fact]
	public void Six_weeks_overdue_reads_as_whole_weeks() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 6, 27, 12, 0), London, Now, true)
			.Should().Be("27 Jun 2026 13:00 (6 weeks overdue)");
}
