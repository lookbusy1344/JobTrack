using NodaTime;
using NodaTime.TimeZones;

namespace dst_spike;

// Spike 5 (plan §5.3 bullet 5): prove Noda Time's ZoneLocalMapping
// resolvers behave as ADR 0008 pins them, against a pinned TZDB version,
// for both a spring-forward gap and an autumn-back fold. Throwaway proof
// code — not production, does not pass through delivery gates.
internal static class Program
{
    private static readonly ZoneLocalMappingResolver Resolver =
        Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnForwardShifted);

    private static int Main()
    {
        var tzdb = DateTimeZoneProviders.Tzdb;
        Console.WriteLine($"TZDB version: {tzdb.VersionId}");

        var london = tzdb["Europe/London"];
        var newYork = tzdb["America/New_York"];

        var failures = 0;

        // Spring-forward gap: Europe/London, 2026-03-29 01:00 -> 02:00 does
        // not exist (clocks jump from 01:00 to 02:00 GMT->BST).
        failures += CheckGap(london, new LocalDateTime(2026, 3, 29, 1, 30, 0));

        // Autumn-back fold: Europe/London, 2026-10-25 01:30 occurs twice
        // (BST then GMT).
        failures += CheckFold(london, new LocalDateTime(2026, 10, 25, 1, 30, 0));

        // Same two cases in a different zone with different transition
        // dates, to confirm the resolver is zone-agnostic.
        failures += CheckGap(newYork, new LocalDateTime(2026, 3, 8, 2, 30, 0));
        failures += CheckFold(newYork, new LocalDateTime(2026, 11, 1, 1, 30, 0));

        // A zone with no DST at all must resolve unambiguously via the
        // same resolver with no special-casing.
        var utc = DateTimeZone.Utc;
        var unambiguous = new LocalDateTime(2026, 6, 15, 12, 0, 0);
        var resolved = unambiguous.InZone(utc, Resolver);
        Console.WriteLine($"No-DST zone (Utc) resolves without adjustment: {resolved.ToInstant()}");

        Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures} check(s) failed)");
        return failures == 0 ? 0 : 1;
    }

    private static int CheckGap(DateTimeZone zone, LocalDateTime gapLocalTime)
    {
        var mapping = zone.MapLocal(gapLocalTime);
        Console.WriteLine($"[{zone.Id}] gap candidate {gapLocalTime}: {mapping.Count} candidate instant(s)");

        if (mapping.Count != 0)
        {
            Console.WriteLine($"  UNEXPECTED: {gapLocalTime} was not actually a gap in {zone.Id} for the pinned TZDB.");
            return 1;
        }

        var resolvedInstant = gapLocalTime.InZone(zone, Resolver).ToInstant();
        var zoneInterval = zone.GetZoneInterval(resolvedInstant);
        Console.WriteLine($"  ForwardShiftResolver resolved to {resolvedInstant} (zone interval starts {zoneInterval.Start})");

        // The forward-shifted instant must land at or after the start of
        // the post-gap zone interval — i.e. genuinely shifted forward past
        // the gap, never before it.
        if (resolvedInstant < zoneInterval.Start)
        {
            Console.WriteLine("  FAIL: resolved instant precedes the post-gap zone interval start.");
            return 1;
        }

        return 0;
    }

    private static int CheckFold(DateTimeZone zone, LocalDateTime foldLocalTime)
    {
        var mapping = zone.MapLocal(foldLocalTime);
        Console.WriteLine($"[{zone.Id}] fold candidate {foldLocalTime}: {mapping.Count} candidate instant(s)");

        if (mapping.Count != 2)
        {
            Console.WriteLine($"  UNEXPECTED: {foldLocalTime} did not have exactly two candidates in {zone.Id} for the pinned TZDB.");
            return 1;
        }

        var earlier = mapping.First().ToInstant();
        var later = mapping.Last().ToInstant();
        var resolvedInstant = foldLocalTime.InZone(zone, Resolver).ToInstant();

        Console.WriteLine($"  candidates: earlier={earlier}, later={later}; resolver picked {resolvedInstant}");

        if (resolvedInstant != earlier)
        {
            Console.WriteLine("  FAIL: resolver did not pick the earlier candidate instant (ADR 0008 requires 'earlier' on fold).");
            return 1;
        }

        return 0;
    }
}
