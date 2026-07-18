namespace JobTrack.Application;

using Abstractions;
using Domain.Costing;

/// <summary>
///     Result of <see cref="ICostQueries.GetCostDetailsAsync" />: one node's exact and penny-rounded
///     displayed cost, together with its canonical explainable segment trace for rate provenance (spec
///     §8.5 workflow 8). <see cref="Trace" /> is already scoped to <see cref="NodeId" />'s subtree by
///     <see
///         cref="CostEngine" />
///     — it never contains a foreign session's identifier, node, or rate (ADR 0017).
/// </summary>
public sealed record CostDetailsResult
{
	/// <summary>The node this result was calculated for.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The exact, unrounded cost of <see cref="NodeId" /> and its subtree.</summary>
	public required Money ExactCost { get; init; }

	/// <summary>The penny-rounded (midpoint-to-even) cost presented to a caller (spec §9).</summary>
	public required Money DisplayedCost { get; init; }

	/// <summary>The canonical segment-by-segment explanation of <see cref="ExactCost" />.</summary>
	public required EquatableArray<CostSegmentTrace> Trace { get; init; }

	/// <summary>
	///     The <c>DateTimeZoneProviders.Tzdb.VersionId</c> in effect when this result was calculated
	///     (ADR 0008/0016). Reproducing this result historically requires this value alongside the
	///     persisted state and <c>asOf</c> — a later TZDB correction to a zone's historical rules can
	///     legitimately change a recalculation, and this is how that is disclosed rather than absorbed.
	/// </summary>
	public required string TzdbVersion { get; init; }
}
