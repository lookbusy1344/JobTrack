namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Result of <see cref="ICostQueries.GetBulkNodeCostsAsync" />: each authorized candidate's displayed
///     (penny-rounded) cost. A requested node id absent from <see cref="DisplayedCosts" /> means the
///     actor may not view that node's individual cost (ADR 0040/0042) -- the caller renders that the same
///     way it already renders any other cost-hidden row, never as an error.
/// </summary>
public sealed record BulkNodeCostResult
{
	/// <summary>Each authorized candidate's displayed cost, keyed by node id.</summary>
	public required EquatableDictionary<JobNodeId, Money> DisplayedCosts { get; init; }
}
