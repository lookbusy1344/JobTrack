using System.Numerics;

namespace cost_sweep_spike;

// Spike 6 (plan §5.3 bullet 6 / §7.2): prove the segment-boundary sweep
// for concurrent 1/N allocation conserves total time exactly, at
// N = 2, 20, 100+, cross-checked against a structurally different
// independent oracle (per-tick brute force, not boundary sweeping).
// Throwaway proof code — not production, does not pass through delivery
// gates; this is not the real cost engine (no schedule/rate resolution),
// only the concurrency/allocation kernel described in §7.2 item 8-9.
internal static class Program
{
    private static int Main()
    {
        var failures = 0;

        failures += RunCase("N=2 (two overlapping sessions)", BuildOverlappingSessions(sessionCount: 2, spanTicks: 120, seed: 1));
        failures += RunCase("N=20 (staggered overlap)", BuildOverlappingSessions(sessionCount: 20, spanTicks: 500, seed: 2));
        failures += RunCase("N=120 (heavy overlap)", BuildOverlappingSessions(sessionCount: 120, spanTicks: 2000, seed: 3));
        failures += RunCase("Disjoint sessions (N=1 throughout)", BuildDisjointSessions(sessionCount: 10, gapTicks: 5, durationTicks: 30));

        Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures} case(s) failed)");
        return failures == 0 ? 0 : 1;
    }

    private static int RunCase(string label, IReadOnlyList<Session> sessions)
    {
        Console.WriteLine($"--- {label}: {sessions.Count} sessions ---");

        var sweepShares = BoundarySweepAllocate(sessions);
        var oracleShares = PerTickOracleAllocate(sessions);

        var failed = false;

        // Exact rational equality between the two structurally different
        // algorithms, per session — no tolerance.
        foreach (var session in sessions)
        {
            var sweep = sweepShares[session.Id];
            var oracle = oracleShares[session.Id];
            if (sweep != oracle)
            {
                Console.WriteLine($"  FAIL: session {session.Id} sweep={sweep} oracle={oracle} (mismatch)");
                failed = true;
            }
        }

        // Conservation: total allocated across all sessions equals the
        // measure of the union of their intervals (the total eligible
        // worked time in this simplified, schedule-free spike).
        var totalAllocated = sweepShares.Values.Aggregate(Rational.Zero, (acc, r) => acc + r);
        var unionDuration = new Rational(MeasureUnion(sessions), 1);
        if (totalAllocated != unionDuration)
        {
            Console.WriteLine($"  FAIL: total allocated {totalAllocated} != union duration {unionDuration}");
            failed = true;
        }
        else
        {
            Console.WriteLine($"  conservation OK: total allocated == union duration == {unionDuration}");
        }

        // No session's allocation may exceed its own duration.
        foreach (var session in sessions)
        {
            var duration = new Rational(session.End - session.Start, 1);
            if (sweepShares[session.Id] > duration)
            {
                Console.WriteLine($"  FAIL: session {session.Id} allocated {sweepShares[session.Id]} exceeds its own duration {duration}");
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    // --- Algorithm 1: boundary sweep (the production approach, §7.2 item 8) ---

    private static Dictionary<int, Rational> BoundarySweepAllocate(IReadOnlyList<Session> sessions)
    {
        var boundaries = sessions.SelectMany(s => new[] { s.Start, s.End }).Distinct().OrderBy(t => t).ToArray();
        var shares = sessions.ToDictionary(s => s.Id, _ => Rational.Zero);

        for (var i = 0; i < boundaries.Length - 1; i++)
        {
            var segmentStart = boundaries[i];
            var segmentEnd = boundaries[i + 1];
            var duration = segmentEnd - segmentStart;
            if (duration <= 0)
            {
                continue;
            }

            var active = sessions.Where(s => s.Start <= segmentStart && s.End >= segmentEnd).ToArray();
            if (active.Length == 0)
            {
                continue;
            }

            var share = new Rational(duration, active.Length);
            foreach (var session in active)
            {
                shares[session.Id] += share;
            }
        }

        return shares;
    }

    // --- Algorithm 2: independent oracle (per-tick brute force, §7.2's
    // "deliberately naive reference implementation that samples cost per
    // instant") ---

    private static Dictionary<int, Rational> PerTickOracleAllocate(IReadOnlyList<Session> sessions)
    {
        var shares = sessions.ToDictionary(s => s.Id, _ => Rational.Zero);
        var minTick = sessions.Min(s => s.Start);
        var maxTick = sessions.Max(s => s.End);

        for (var tick = minTick; tick < maxTick; tick++)
        {
            var active = sessions.Where(s => s.Start <= tick && s.End > tick).ToArray();
            if (active.Length == 0)
            {
                continue;
            }

            var share = new Rational(1, active.Length);
            foreach (var session in active)
            {
                shares[session.Id] += share;
            }
        }

        return shares;
    }

    private static long MeasureUnion(IReadOnlyList<Session> sessions)
    {
        var ordered = sessions.OrderBy(s => s.Start).ToArray();
        long total = 0;
        long? currentStart = null;
        long currentEnd = 0;

        foreach (var session in ordered)
        {
            if (currentStart is null)
            {
                currentStart = session.Start;
                currentEnd = session.End;
                continue;
            }

            if (session.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, session.End);
            }
            else
            {
                total += currentEnd - currentStart.Value;
                currentStart = session.Start;
                currentEnd = session.End;
            }
        }

        if (currentStart is not null)
        {
            total += currentEnd - currentStart.Value;
        }

        return total;
    }

    private static IReadOnlyList<Session> BuildOverlappingSessions(int sessionCount, long spanTicks, int seed)
    {
        var random = new Random(seed);
        var sessions = new List<Session>();
        for (var i = 0; i < sessionCount; i++)
        {
            var start = random.NextInt64(0, spanTicks / 2);
            var duration = random.NextInt64(spanTicks / 4, spanTicks);
            sessions.Add(new Session(i, start, start + duration));
        }

        return sessions;
    }

    private static IReadOnlyList<Session> BuildDisjointSessions(int sessionCount, long gapTicks, long durationTicks)
    {
        var sessions = new List<Session>();
        long cursor = 0;
        for (var i = 0; i < sessionCount; i++)
        {
            sessions.Add(new Session(i, cursor, cursor + durationTicks));
            cursor += durationTicks + gapTicks;
        }

        return sessions;
    }
}

internal sealed record Session(int Id, long Start, long End);

// Exact rational, reduced via GCD on every operation — mirrors ADR 0009's
// "carried as the exact rational (segmentTicks, N)" requirement; no
// floating point anywhere in this spike.
internal readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
{
    public static readonly Rational Zero = new(0, 1);

    private readonly BigInteger _numerator;
    private readonly BigInteger _denominator;

    public Rational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must not be zero.");
        }

        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        if (gcd == 0)
        {
            gcd = 1;
        }

        _numerator = numerator / gcd;
        _denominator = denominator / gcd;
    }

    public static Rational operator +(Rational left, Rational right) =>
        new(left._numerator * right._denominator + right._numerator * left._denominator, left._denominator * right._denominator);

    public static bool operator ==(Rational left, Rational right) => left.Equals(right);

    public static bool operator !=(Rational left, Rational right) => !left.Equals(right);

    public static bool operator >(Rational left, Rational right) =>
        left._numerator * right._denominator > right._numerator * left._denominator;

    public static bool operator <(Rational left, Rational right) =>
        left._numerator * right._denominator < right._numerator * left._denominator;

    public bool Equals(Rational other) => _numerator == other._numerator && _denominator == other._denominator;

    public override bool Equals(object? obj) => obj is Rational other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_numerator, _denominator);

    public int CompareTo(Rational other) => (this > other) ? 1 : (this < other) ? -1 : 0;

    public override string ToString() => $"{_numerator}/{_denominator}";
}
