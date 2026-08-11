namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using NodaTime;

/// <summary>
///     <see cref="DurationDisplay" />'s elapsed wall-clock rendering, used by the concurrent-work
///     table's overlap column.
/// </summary>
public sealed class DurationDisplayTests
{
	[Fact]
	public void Whole_hours_and_minutes_read_as_hours_and_minutes() =>
		DurationDisplay.Format(Duration.FromMinutes(200)).Should().Be("3h 20m");

	[Fact]
	public void Under_an_hour_drops_the_hours_part() => DurationDisplay.Format(Duration.FromMinutes(45)).Should().Be("45m");

	[Fact]
	public void A_whole_number_of_hours_still_names_its_zero_minutes() =>
		DurationDisplay.Format(Duration.FromHours(2)).Should().Be("2h 0m");

	[Fact]
	public void Less_than_a_minute_reads_as_zero_minutes_rather_than_zero_hours() =>
		DurationDisplay.Format(Duration.FromSeconds(30)).Should().Be("0m");

	[Fact]
	public void Seconds_are_truncated_rather_than_rounded_up() =>
		DurationDisplay.Format(Duration.FromMinutes(90) + Duration.FromSeconds(59)).Should().Be("1h 30m");
}
