namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Input to <see cref="ICostQueries.GetBulkNodeCostsAsync" />.</summary>
public sealed record GetBulkNodeCostsRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>
	///     The candidate node ids to price, already filtered by the caller's own row-level visibility
	///     check (e.g. <see cref="Domain.Authorization.CostAccessPolicy.CanViewNodeCost" />) -- this
	///     request narrows further per ADR 0040's ancestor-ownership gate, but never broadens visibility
	///     beyond what the caller already decided a row may show.
	/// </summary>
	public required EquatableArray<JobNodeId> NodeIds { get; init; }

	/// <summary>The single captured instant every candidate is costed as of.</summary>
	public required Instant AsOf { get; init; }
}
