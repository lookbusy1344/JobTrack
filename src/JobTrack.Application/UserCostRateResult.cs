namespace JobTrack.Application;

using Abstractions;
using Domain.Rates;
using NodaTime;

/// <summary>Result of <see cref="IRateCommands.AddUserCostRateAsync" />.</summary>
public sealed record UserCostRateResult
{
	/// <summary>The user cost rate's identifier.</summary>
	public required UserCostRateId Id { get; init; }

	/// <summary>The employee this cost rate belongs to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The effective-dated rate.</summary>
	public required UserCostRate Rate { get; init; }

	/// <summary>The instant this cost rate was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The cost rate's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
