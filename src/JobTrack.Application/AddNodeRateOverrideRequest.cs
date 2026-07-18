namespace JobTrack.Application;

using Abstractions;
using Domain.Rates;

/// <summary>
///     Input to <see cref="IRateCommands.AddNodeRateOverrideAsync" /> (spec §9.2). Reuses the pure
///     <see cref="NodeRateOverride" /> domain value directly — its own constructor already enforces that
///     <c>EffectiveEnd</c> strictly follows <c>EffectiveStart</c>. The worker the override applies to is
///     carried separately from the override value, the same shape as <see cref="AddScheduleVersionRequest" />'s
///     separate <c>UserId</c>, because <see cref="NodeRateOverride" /> is scoped to one worker only by
///     the persistence key, not by a field on the value itself (see <see cref="Domain.Rates.RateResolver" />).
/// </summary>
public sealed record AddNodeRateOverrideRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The worker this override applies to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The node, rate, and effective range of the override.</summary>
	public required NodeRateOverride Override { get; init; }
}
