namespace JobTrack.Web;

using Abstractions;
using Domain.Costing;

/// <summary>Formats the paired monetary and allocated-time values of an actual labour cost.</summary>
internal static class CostDisplay
{
	/// <summary>Stands in for a zero cost in a table cell so it doesn't read as "nothing recorded".</summary>
	private const string ZeroCostPlaceholder = "-";

	/// <summary>
	///     A non-breaking space between the separator and the duration, so a line wrap (this text sits in
	///     a narrow table cell) always falls before the "/" rather than stranding it against the hours.
	/// </summary>
	private const char NonBreakingSpace = ' ';

	/// <summary>
	///     Renders the figure for a table cell, standing a zero cost down to a dash: among a column of
	///     real amounts a &#163;0.00 reads as "nothing recorded here" rather than as a cost that is
	///     genuinely nil.
	/// </summary>
	internal static string FormatCell(Money cost, AllocatedDuration allocatedDuration) =>
		cost.Amount == 0m ? ZeroCostPlaceholder : FormatField(cost, allocatedDuration);

	/// <summary>
	///     Renders the figure for a record-card field, stating a zero cost as the figure it is. A
	///     labelled field on one node has no column of neighbours to be mistaken for, so the ambiguity
	///     the dash exists to resolve never arises -- while the dash itself reads there as if the cost
	///     were unavailable, which is a different claim from nil.
	/// </summary>
	internal static string FormatField(Money cost, AllocatedDuration allocatedDuration) =>
		$"{cost} /{NonBreakingSpace}{allocatedDuration}";
}
