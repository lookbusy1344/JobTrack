namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IRequestCommands.AcknowledgeAsync" /> (ADR 0034): sets
///     <c>job_request.acknowledged_at</c>/<c>acknowledged_by_user_id</c> once, giving requesters an
///     explicit "seen by IT" signal distinct from <see cref="RequesterStatus.Submitted" />.
/// </summary>
public sealed record AcknowledgeJobRequestRequest
{
	/// <summary>The acting staff user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The requester-originated node being acknowledged. Must have an associated <c>job_request</c> row.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The expected current optimistic-concurrency version of the <c>job_request</c> row.</summary>
	public required long Version { get; init; }
}
