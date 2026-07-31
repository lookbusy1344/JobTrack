namespace JobTrack.Web;

using Abstractions;
using Domain.Costing;

/// <summary>Formats the paired monetary and allocated-time values of an actual labour cost.</summary>
internal static class CostDisplay
{
	/// <summary>Stands in for a zero cost so it doesn't read as "nothing recorded".</summary>
	private const string ZeroCostPlaceholder = "-";

	/// <summary>
	///     A non-breaking space between the separator and the duration, so a line wrap (this text sits in
	///     a narrow table cell) always falls before the "/" rather than stranding it against the hours.
	/// </summary>
	private const char NonBreakingSpace = ' ';

	internal static string Format(Money cost, AllocatedDuration allocatedDuration) =>
		cost.Amount == 0m ? ZeroCostPlaceholder : $"{cost} /{NonBreakingSpace}{allocatedDuration}";
}
