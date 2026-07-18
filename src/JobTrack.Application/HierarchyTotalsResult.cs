namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Result of <see cref="ICostQueries.GetHierarchyTotalsAsync" />: <see cref="NodeId" /> and every
///     node in its subtree, with both the exact cost and the penny-rounded displayed cost reconciled at
///     each hierarchy level (ADR 0002) so a node's children's displayed amounts always sum exactly to
///     its own displayed amount.
/// </summary>
public sealed record HierarchyTotalsResult
{
	/// <summary>The subtree root this result was calculated for.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>Each node's exact, unrounded cost, keyed by identifier.</summary>
	public required EquatableDictionary<JobNodeId, Money> ExactCosts { get; init; }

	/// <summary>Each node's penny-rounded displayed cost, reconciled level by level, keyed by identifier.</summary>
	public required EquatableDictionary<JobNodeId, Money> DisplayedCosts { get; init; }

	/// <summary>
	///     The <c>DateTimeZoneProviders.Tzdb.VersionId</c> in effect when this result was calculated
	///     (ADR 0008/0016). Reproducing this result historically requires this value alongside the
	///     persisted state and <c>asOf</c> — a later TZDB correction to a zone's historical rules can
	///     legitimately change a recalculation, and this is how that is disclosed rather than absorbed.
	/// </summary>
	public required string TzdbVersion { get; init; }
}
