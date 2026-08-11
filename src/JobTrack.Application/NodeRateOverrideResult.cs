namespace JobTrack.Application;

using Abstractions;
using Domain.Rates;
using NodaTime;

/// <summary>Result of <see cref="IRateCommands.AddNodeRateOverrideAsync" />.</summary>
public sealed record NodeRateOverrideResult
{
	/// <summary>The override's identifier.</summary>
	public required NodeRateOverrideId Id { get; init; }

	/// <summary>The worker this override applies to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The node, rate, and effective range of the override.</summary>
	public required NodeRateOverride Override { get; init; }

	/// <summary>The instant this override was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The override's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
