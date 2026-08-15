namespace JobTrack.Domain.Intervals;

using Abstractions;
using NodaTime;

/// <summary>
///     A finite half-open time interval <c>[Start, End)</c> (spec §4/§10.2.1): it includes its start
///     instant and excludes its end instant, so two intervals that merely touch at a boundary have no
///     overlap. The building block for session, working-time, and schedule-exception algebra.
/// </summary>
[LargeStruct(
	"Two NodaTime Instants at 16 bytes each (32 bytes total): WorkInterval is the domain's core " +
	"interval primitive (spec §4/§10.2.1), and every session, working-time, and schedule-exception " +
	"algorithm passes it by value throughout. Reviewed and accepted -- narrowing the representation " +
	"would mean re-deriving that algebra on a different primitive, not a local fix.")]
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
