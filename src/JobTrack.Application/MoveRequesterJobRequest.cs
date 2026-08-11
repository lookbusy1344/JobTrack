namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IRequestCommands.MoveAsync" /> (ADR 0033, plan §5). Distinct from
///     <see cref="MoveJobNodeRequest" />: <c>canMoveRequesterJob</c> authorizes on control of
///     <see cref="NodeId" /> alone, deliberately not also requiring control of
///     <see cref="NewParentId" /> — the routine intake workflow re-homes a claimed holding-area request
///     under whichever branch the problem turns out to belong to, which the destination-control check
///     on the ordinary structural move would otherwise block.
/// </summary>
public sealed record MoveRequesterJobRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The requester-originated node being moved. Must have an associated <c>job_request</c> row.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The destination parent.</summary>
	public required JobNodeId NewParentId { get; init; }

	/// <summary>The expected current optimistic-concurrency version of <see cref="NodeId" />.</summary>
	public required long Version { get; init; }
}
