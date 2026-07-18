namespace JobTrack.Application;

using Abstractions;
using Domain.Rates;

/// <summary>
///     Input to <see cref="IRateCommands.CorrectUserCostRateAsync" /> (ADR 0003: historical user cost
///     rates may be corrected in place by a Rate manager or Administrator). Reuses the pure
///     <see cref="UserCostRate" /> domain value directly, the same shape as
///     <see cref="AddUserCostRateRequest" />.
/// </summary>
public sealed record CorrectUserCostRateRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The cost rate being corrected.</summary>
	public required UserCostRateId RateId { get; init; }

	/// <summary>
	///     Optional nested-route cross-check: if set and it does not match the loaded row's owner, the
	///     correction is treated as if the row does not exist (mirrors <see cref="CorrectSessionRequest.LeafWorkId" />).
	/// </summary>
	public AppUserId? UserId { get; init; }

	/// <summary>The expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>Why this cost rate is being corrected.</summary>
	public required string Reason { get; init; }

	/// <summary>The corrected effective range and rate.</summary>
	public required UserCostRate Rate { get; init; }
}
