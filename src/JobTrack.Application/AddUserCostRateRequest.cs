namespace JobTrack.Application;

using Abstractions;
using Domain.Rates;

/// <summary>
///     Input to <see cref="IRateCommands.AddUserCostRateAsync" />. Reuses the pure
///     <see cref="UserCostRate" /> domain value directly — its own constructor already enforces that
///     <c>EffectiveEnd</c> strictly follows <c>EffectiveStart</c>.
/// </summary>
public sealed record AddUserCostRateRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee this cost rate belongs to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The effective-dated rate being added.</summary>
	public required UserCostRate Rate { get; init; }
}
