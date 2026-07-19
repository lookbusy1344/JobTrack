namespace JobTrack.Domain.Rates;

using Abstractions;
using NodaTime;

/// <summary>
///     One effective-dated hourly rate override for a particular node and worker (spec §9.2). Applies
///     to <see cref="NodeId" /> and every descendant during its effective range unless a closer
///     descendant defines its own override for the same worker at the costed instant (the effective
///     nearest-ancestor rule, applied by <see cref="RateResolver" />).
/// </summary>
public sealed record NodeRateOverride
{
	/// <summary>Creates a <see cref="NodeRateOverride" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="effectiveEnd" /> does not strictly follow <paramref name="effectiveStart" />.</exception>
	public NodeRateOverride(JobNodeId nodeId, HourlyRate rate, Instant effectiveStart, Instant? effectiveEnd)
	{
		if (effectiveEnd is Instant end && end <= effectiveStart) {
			throw new ArgumentOutOfRangeException(nameof(effectiveEnd), effectiveEnd,
				"An effective end instant must be strictly after the effective start instant.");
		}

		NodeId = nodeId;
		Rate = rate;
		EffectiveStart = effectiveStart;
		EffectiveEnd = effectiveEnd;
	}

	/// <summary>The node this override applies to, and to every descendant absent a closer override.</summary>
	public JobNodeId NodeId { get; }

	/// <summary>The overriding hourly rate.</summary>
	public HourlyRate Rate { get; }

	/// <summary>The inclusive instant this override takes effect.</summary>
	public Instant EffectiveStart { get; }

	/// <summary>The exclusive instant this override stops applying, or <see langword="null" /> if still current.</summary>
	public Instant? EffectiveEnd { get; }

	/// <summary>Whether <paramref name="at" /> falls within <see cref="EffectiveStart" />/<see cref="EffectiveEnd" />.</summary>
	public bool IsEffectiveAt(Instant at) => at >= EffectiveStart && (EffectiveEnd is not Instant end || at < end);
}
