namespace JobTrack.Application;

using Abstractions;
using Domain.Rates;

/// <summary>
///     Input to <see cref="IRateCommands.CorrectNodeRateOverrideAsync" /> (ADR 0003: historical node rate
///     overrides may be corrected in place by a Rate manager or Administrator). Reuses the pure
///     <see cref="NodeRateOverride" /> domain value directly, the same shape as
///     <see cref="AddNodeRateOverrideRequest" />.
/// </summary>
public sealed record CorrectNodeRateOverrideRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node rate override being corrected.</summary>
	public required NodeRateOverrideId OverrideId { get; init; }

	/// <summary>
	///     Optional nested-route cross-check: if set and it does not match the loaded row's owner, the
	///     correction is treated as if the row does not exist (mirrors <see cref="CorrectSessionRequest.LeafWorkId" />).
	/// </summary>
	public AppUserId? UserId { get; init; }

	/// <summary>The expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>Why this override is being corrected.</summary>
	public required string Reason { get; init; }

	/// <summary>The corrected node, rate, and effective range.</summary>
	public required NodeRateOverride Override { get; init; }
}
