namespace JobTrack.Domain.Costing;

/// <summary>
///     One active session's exact equal share of a constant-membership segment (spec §10.2/§10.3 step
///     10; ADR 0009): the segment's duration in ticks and the concurrency divisor <c>N</c>, kept as the
///     unreduced pair <c>(segmentTicks, N)</c> rather than a reduced fraction or rounded value — the
///     share is <c>SegmentTicks / ConcurrencyDivisor</c>, computed exactly wherever it is later
///     consumed, never rounded to whole ticks here.
/// </summary>
public readonly record struct AllocatedShare
{
	/// <summary>Creates an <see cref="AllocatedShare" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentTicks" /> or <paramref name="concurrencyDivisor" /> is not positive.</exception>
	public AllocatedShare(long segmentTicks, int concurrencyDivisor)
	{
		if (segmentTicks <= 0) {
			throw new ArgumentOutOfRangeException(nameof(segmentTicks), segmentTicks, "A segment's tick count must be positive.");
		}

		if (concurrencyDivisor <= 0) {
			throw new ArgumentOutOfRangeException(nameof(concurrencyDivisor), concurrencyDivisor, "A concurrency divisor must be positive.");
		}

		SegmentTicks = segmentTicks;
		ConcurrencyDivisor = concurrencyDivisor;
	}

	/// <summary>The constant-membership segment's duration, in 100-nanosecond ticks (ADR 0007).</summary>
	public long SegmentTicks { get; }

	/// <summary>The number of active sessions sharing the segment, <c>N</c>.</summary>
	public int ConcurrencyDivisor { get; }
}
