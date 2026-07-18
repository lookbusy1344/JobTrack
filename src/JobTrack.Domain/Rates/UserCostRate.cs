namespace JobTrack.Domain.Rates;

using Abstractions;
using NodaTime;

/// <summary>One of a user's effective-dated hourly cost rates (spec §9.1).</summary>
public sealed record UserCostRate
{
	/// <summary>Creates a <see cref="UserCostRate" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="effectiveEnd" /> does not strictly follow <paramref name="effectiveStart" />.</exception>
	public UserCostRate(HourlyRate rate, Instant effectiveStart, Instant? effectiveEnd)
	{
		if (effectiveEnd is { } end && end <= effectiveStart) {
			throw new ArgumentOutOfRangeException(nameof(effectiveEnd), effectiveEnd,
				"An effective end instant must be strictly after the effective start instant.");
		}

		Rate = rate;
		EffectiveStart = effectiveStart;
		EffectiveEnd = effectiveEnd;
	}

	/// <summary>The rate in effect for this range.</summary>
	public HourlyRate Rate { get; }

	/// <summary>The inclusive instant this rate takes effect.</summary>
	public Instant EffectiveStart { get; }

	/// <summary>The exclusive instant this rate stops applying, or <see langword="null" /> if still current.</summary>
	public Instant? EffectiveEnd { get; }

	/// <summary>Whether <paramref name="at" /> falls within <see cref="EffectiveStart" />/<see cref="EffectiveEnd" />.</summary>
	public bool IsEffectiveAt(Instant at) => at >= EffectiveStart && (EffectiveEnd is not { } end || at < end);
}
