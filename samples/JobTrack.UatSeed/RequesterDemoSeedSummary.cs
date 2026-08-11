namespace JobTrack.UatSeed;

using Abstractions;

/// <summary>
///     Identifiers created by <see cref="UatSeeder.SeedRequesterDemoAsync" />, in the stable order
///     Submitted, Accepted, Waiting, In progress, Completed, Cancelled.
/// </summary>
public sealed record RequesterDemoSeedSummary
{
	/// <summary>The six requester-owned job-request anchors.</summary>
	public required EquatableArray<JobNodeId> RequestNodeIds { get; init; }
}
