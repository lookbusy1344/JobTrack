namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Request-scoped filter for <see cref="IAwaitingProgressQueryPort.GetAwaitingProgressInputsAsync" />
///     (2026-07-25 scalability-follow-up plan §2.1): ownership, an optional subtree root, normalized
///     search text, and offset/limit paging, applied by the port's own query against the exact
///     priority/deadline/id ordering <c>AwaitingProgressCalculator</c> used to apply in memory. The
///     calculator receives only the resulting page and must not reapply any of these filters.
/// </summary>
internal sealed record AwaitingProgressQueryFilter
{
	public required OwnershipFilter Ownership { get; init; }

	public JobNodeId? SubtreeRootId { get; init; }

	public string? SearchText { get; init; }

	public required int Offset { get; init; }

	public required int Limit { get; init; }
}
