namespace JobTrack.Domain.Intervals;

using NodaTime;

/// <summary>
///     A finite half-open time interval <c>[Start, End)</c> (spec §4/§10.2.1): it includes its start
///     instant and excludes its end instant, so two intervals that merely touch at a boundary have no
///     overlap. The building block for session, working-time, and schedule-exception algebra.
/// </summary>
public readonly record struct WorkInterval
{
	/// <summary>Creates a <see cref="WorkInterval" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="end" /> does not strictly follow <paramref name="start" />.</exception>
	public WorkInterval(Instant start, Instant end)
	{
		if (end <= start) {
			throw new ArgumentOutOfRangeException(nameof(end), end, "An interval's end must be strictly after its start.");
		}

		Start = start;
		End = end;
	}

	/// <summary>The inclusive start instant.</summary>
	public Instant Start { get; }

	/// <summary>The exclusive end instant.</summary>
	public Instant End { get; }

	/// <summary>The interval's duration, <c>End - Start</c>.</summary>
	public Duration Duration => End - Start;

	/// <summary>Whether <paramref name="instant" /> falls within this half-open interval.</summary>
	public bool Contains(Instant instant) => instant >= Start && instant < End;
}
