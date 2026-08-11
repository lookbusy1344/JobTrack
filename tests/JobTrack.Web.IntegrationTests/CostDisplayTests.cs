namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Domain.Costing;

/// <summary>
///     <see cref="CostDisplay" />'s two renderings of one costed figure, which differ only in how they
///     say "zero". A table cell says it with a dash, because a &#163;0.00 among a column of real
///     figures reads as "nothing recorded here"; a record-card field says it with the figure, because
///     a labelled field on one node has no column of neighbours to be mistaken for, and a dash there
///     reads as if the cost were unavailable rather than nil.
/// </summary>
public sealed class CostDisplayTests
{
	private const int OneWorker = 1;
	private const string NonBreakingSpace = " ";

	private static readonly AllocatedDuration EightHours =
		AllocatedDuration.FromShare(new(8 * TimeSpan.TicksPerHour, OneWorker));

	[Fact]
	public void A_table_cell_renders_a_real_cost_as_the_amount_and_its_allocated_time() =>
		CostDisplay.FormatCell(new(200m), EightHours).Should().Be($"£200.00 /{NonBreakingSpace}8.0 hrs");

	[Fact]
	public void A_record_field_renders_a_real_cost_identically_to_a_table_cell() =>
		CostDisplay.FormatField(new(200m), EightHours).Should().Be(CostDisplay.FormatCell(new(200m), EightHours));

	[Fact]
	public void A_table_cell_stands_a_zero_cost_down_to_a_dash() =>
		CostDisplay.FormatCell(new(0m), EightHours).Should().Be("-");

	[Fact]
	public void A_record_field_states_a_zero_cost_as_the_figure_it_is() =>
		CostDisplay.FormatField(new(0m), EightHours).Should().Be($"£0.00 /{NonBreakingSpace}8.0 hrs");
}
