namespace JobTrack.AdminCli.Tests;

using Abstractions;
using Application;
using AwesomeAssertions;
using NodaTime;

/// <summary>
///     Unit tests for <see cref="JobTreeImportWork" /> — the mapping from an <c>import-tree</c> JSON
///     row's work fields onto an <see cref="Application.ImportSubtreeLeafWorkSpec" />. Covers both
///     spellings (relative <c>open</c>/<c>closed</c> durations resolved against the import instant, and
///     absolute <c>start</c>/<c>end</c> timestamps) and every way the two can be combined wrongly.
/// </summary>
public sealed class JobTreeImportWorkTests
{
	private static readonly Instant ImportedAt = Instant.FromUtc(2026, 7, 18, 9, 0, 0);
	private static readonly AppUserId Worker = new(7);

	[Fact]
	public void A_row_with_no_work_fields_resolves_to_no_work() => Resolve().Should().BeNull();

	[Fact]
	public void An_open_row_starts_the_given_duration_before_the_import_and_stays_in_progress()
	{
		var work = Resolve("2 days");

		work.Should().NotBeNull();
		work!.StartedAt.Should().Be(ImportedAt - Duration.FromDays(2));
		work.FinishedAt.Should().BeNull();
		work.Achievement.Should().Be(Achievement.InProgress);
		work.WorkedByUserId.Should().Be(Worker);
	}

	[Fact]
	public void A_closed_row_spans_both_durations_before_the_import_and_succeeds()
	{
		var work = Resolve("2 days", "1 day");

		work.Should().NotBeNull();
		work!.StartedAt.Should().Be(ImportedAt - Duration.FromDays(2));
		work.FinishedAt.Should().Be(ImportedAt - Duration.FromDays(1));
		work.Achievement.Should().Be(Achievement.Success);
	}

	[Theory]
	[InlineData("90 minutes", 90 * NodaConstants.TicksPerMinute)]
	[InlineData("36 hours", 36 * NodaConstants.TicksPerHour)]
	[InlineData("2 weeks", 14 * NodaConstants.TicksPerDay)]
	[InlineData("1.5 days", 36 * NodaConstants.TicksPerHour)]
	[InlineData("3d", 3 * NodaConstants.TicksPerDay)]
	[InlineData("4h", 4 * NodaConstants.TicksPerHour)]
	[InlineData("  5   days  ", 5 * NodaConstants.TicksPerDay)]
	public void Relative_durations_accept_the_documented_units_and_spellings(string text, long expectedTicks)
	{
		var work = Resolve(text);

		work!.StartedAt.Should().Be(ImportedAt - Duration.FromTicks(expectedTicks));
	}

	[Theory]
	[InlineData("")]
	[InlineData("soon")]
	[InlineData("2")]
	[InlineData("days")]
	[InlineData("2 fortnights")]
	[InlineData("-2 days")]
	public void An_unparseable_relative_duration_is_rejected(string text)
	{
		var act = () => Resolve(text);

		act.Should().Throw<AdminCliUsageException>().WithMessage("*open*");
	}

	[Fact]
	public void Absolute_start_and_end_are_taken_verbatim()
	{
		var work = Resolve(start: "2026-07-10T09:00:00Z", end: "2026-07-10T17:30:00Z");

		work.Should().NotBeNull();
		work!.StartedAt.Should().Be(Instant.FromUtc(2026, 7, 10, 9, 0, 0));
		work.FinishedAt.Should().Be(Instant.FromUtc(2026, 7, 10, 17, 30, 0));
		work.Achievement.Should().Be(Achievement.Success);
	}

	[Fact]
	public void An_absolute_offset_is_honoured_rather_than_assumed_to_be_utc()
	{
		var work = Resolve(start: "2026-07-10T09:00:00+01:00");

		work!.StartedAt.Should().Be(Instant.FromUtc(2026, 7, 10, 8, 0, 0));
		work.Achievement.Should().Be(Achievement.InProgress);
	}

	[Theory]
	[InlineData("2026-07-10")]
	[InlineData("2026-07-10T09:00:00")]
	[InlineData("10/07/2026 09:00")]
	[InlineData("not a date")]
	public void An_absolute_timestamp_without_an_unambiguous_offset_is_rejected(string text)
	{
		var act = () => Resolve(start: text);

		act.Should().Throw<AdminCliUsageException>().WithMessage("*start*");
	}

	[Theory]
	[InlineData("success", Achievement.Success)]
	[InlineData("Success", Achievement.Success)]
	[InlineData("cancelled", Achievement.Cancelled)]
	[InlineData("canceled", Achievement.Cancelled)]
	[InlineData("unsuccessful", Achievement.Unsuccessful)]
	public void An_explicit_outcome_sets_the_closed_leafs_achievement(string outcome, Achievement expected)
	{
		var work = Resolve("2 days", "1 day", outcome: outcome);

		work!.Achievement.Should().Be(expected);
	}

	[Fact]
	public void An_unrecognised_outcome_is_rejected()
	{
		var act = () => Resolve("2 days", "1 day", outcome: "nearly");

		act.Should().Throw<AdminCliUsageException>().WithMessage("*outcome*");
	}

	[Fact]
	public void An_outcome_on_a_job_that_never_closes_is_rejected()
	{
		var act = () => Resolve("2 days", outcome: "success");

		act.Should().Throw<AdminCliUsageException>().WithMessage("*outcome*");
	}

	[Fact]
	public void An_outcome_with_no_work_at_all_is_rejected()
	{
		var act = () => Resolve(outcome: "success");

		act.Should().Throw<AdminCliUsageException>().WithMessage("*outcome*");
	}

	[Fact]
	public void Mixing_relative_and_absolute_spellings_is_rejected()
	{
		var act = () => Resolve("2 days", end: "2026-07-17T09:00:00Z");

		act.Should().Throw<AdminCliUsageException>().WithMessage("*cannot mix*");
	}

	[Fact]
	public void Closing_a_job_that_was_never_opened_is_rejected()
	{
		var act = () => Resolve(closed: "1 day");

		act.Should().Throw<AdminCliUsageException>().WithMessage("*open*");
	}

	[Fact]
	public void Ending_a_job_that_was_never_started_is_rejected()
	{
		var act = () => Resolve(end: "2026-07-17T09:00:00Z");

		act.Should().Throw<AdminCliUsageException>().WithMessage("*start*");
	}

	private static ImportSubtreeLeafWorkSpec? Resolve(
		string? open = null, string? closed = null, string? start = null, string? end = null, string? outcome = null) =>
		JobTreeImportWork.Resolve(
			new() {
				Id = 1,
				Title = "A node",
				Open = open,
				Closed = closed,
				Start = start,
				End = end,
				Outcome = outcome,
			},
			ImportedAt,
			Worker);
}
