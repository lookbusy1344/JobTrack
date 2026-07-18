namespace JobTrack.Domain.Costing;

using Abstractions;
using NodaTime;

/// <summary>
///     Computes one session's monetary contribution for one constant-rate segment (spec §10.3 step 12;
///     ADR 0009): the single rounded division <c>rate × segmentTicks ÷ (N × ticksPerHour)</c>, computed
///     once directly to <see cref="decimal" /> — never <c>round(share) × rate</c>, which would reintroduce
///     the conservation error the exact <see cref="AllocatedShare" /> pair was built to avoid.
/// </summary>
public static class SegmentCostCalculator
{
	private static readonly long TicksPerHour = Duration.FromHours(1).BclCompatibleTicks;

	/// <summary>Computes the monetary contribution of <paramref name="share" /> at <paramref name="rate" />.</summary>
	public static Money Calculate(AllocatedShare share, HourlyRate rate) =>
		new(rate.AmountPerHour * share.SegmentTicks / (share.ConcurrencyDivisor * TicksPerHour));
}
