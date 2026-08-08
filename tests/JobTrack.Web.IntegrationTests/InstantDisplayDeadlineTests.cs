namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using NodaTime;

/// <summary>
///     <see cref="InstantDisplay.FormatDeadline" />: the record-card deadline field's own rendering --
///     the full local date and time, followed by how much of it is left, coarsening from whole days to
///     whole hours and then to nothing at all as the deadline approaches.
/// </summary>
public sealed class InstantDisplayDeadlineTests
{
	private static readonly DateTimeZone London = DateTimeZoneProviders.Tzdb["Europe/London"];
	private static readonly Instant Now = Instant.FromUtc(2026, 8, 8, 12, 0);

	[Fact]
	public void A_deadline_days_away_reads_as_a_local_stamp_and_whole_days_left() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 10, 15, 0), London, Now, isOpen: true)
			.Should().Be("10 Aug 2026 16:00 (2 days)");

	[Fact]
	public void Part_days_are_truncated_rather_than_rounded_up() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 11, 11, 0), London, Now, isOpen: true)
			.Should().Be("11 Aug 2026 12:00 (2 days)");

	[Fact]
	public void Under_two_days_left_reads_as_whole_hours() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 10, 10, 30), London, Now, isOpen: true)
			.Should().Be("10 Aug 2026 11:30 (46 hours)");

	[Fact]
	public void A_single_hour_left_reads_in_the_singular() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 13, 30), London, Now, isOpen: true)
			.Should().Be("8 Aug 2026 14:30 (1 hour)");

	[Fact]
	public void Under_an_hour_left_shows_the_stamp_alone() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 12, 59), London, Now, isOpen: true)
			.Should().Be("8 Aug 2026 13:59");

	[Fact]
	public void An_open_job_days_past_its_deadline_says_how_many_days_overdue() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 1, 9, 0), London, Now, isOpen: true)
			.Should().Be("1 Aug 2026 10:00 (7 days overdue)");

	[Fact]
	public void Under_two_days_overdue_reads_as_whole_hours() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 6, 14, 30), London, Now, isOpen: true)
			.Should().Be("6 Aug 2026 15:30 (45 hours overdue)");

	[Fact]
	public void A_single_hour_overdue_reads_in_the_singular() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 10, 30), London, Now, isOpen: true)
			.Should().Be("8 Aug 2026 11:30 (1 hour overdue)");

	[Fact]
	public void Under_an_hour_overdue_shows_the_stamp_alone() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 8, 11, 1), London, Now, isOpen: true)
			.Should().Be("8 Aug 2026 12:01");

	[Fact]
	/// <summary>
	///     Overdue is a live alarm, so a job that has ended never carries it -- the same rule that keeps
	///     a closed job's missed deadline out of red.
	/// </summary>
	public void A_closed_job_past_its_deadline_shows_the_stamp_alone() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 1, 9, 0), London, Now, isOpen: false)
			.Should().Be("1 Aug 2026 10:00");

	[Fact]
	/// <summary>Time still to run is a plain fact, reported whether the job has ended or not.</summary>
	public void A_closed_job_short_of_its_deadline_still_reports_the_time_left() =>
		InstantDisplay.FormatDeadline(Instant.FromUtc(2026, 8, 10, 15, 0), London, Now, isOpen: false)
			.Should().Be("10 Aug 2026 16:00 (2 days)");
}
