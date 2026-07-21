namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetSessionManageCapabilitiesAsync" />. Batch-by-ids, mirroring
///     <see cref="GetActiveSessionsRequest" />'s shape (ADR 0044/Stage 4 of the browse-sessions plan:
///     one batched read of whether the actor may manage sessions on each of the given leaves, so Razor
///     can decide what to render — a "Start for…" disclosure, an authorized finish for another worker
///     — without a per-row ancestor-ownership query).
/// </summary>
public sealed record GetSessionManageCapabilitiesRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaves to check the actor's session-management capability for.</summary>
	public required EquatableArray<JobNodeId> LeafWorkIds { get; init; }
}
