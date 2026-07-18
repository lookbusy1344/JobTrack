namespace JobTrack.Domain.Schedules;

using NodaTime;
using NodaTime.TimeZones;

/// <summary>
///     The single shared civil-time-to-instant resolver used everywhere a weekly schedule interval or
///     schedule exception boundary (both defined in local/wall-clock time) is mapped to an
///     <see cref="Instant" /> (ADR 0008): a spring-forward gap shifts the local time forward by the
///     gap's length, and an autumn-back fold resolves to the earlier of the two candidate instants.
///     Wired as one named instance so a future policy change is a one-line edit with full test
///     coverage, not a scattered find-and-replace.
/// </summary>
public static class CivilTimeResolver
{
	/// <summary>The composed resolver: earlier occurrence on a fold, forward shift through a gap.</summary>
	public static readonly ZoneLocalMappingResolver Resolve =
		Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnForwardShifted);

	/// <summary>Maps <paramref name="localDateTime" /> to its <see cref="Instant" /> in <paramref name="zone" /> using <see cref="Resolve" />.</summary>
	public static Instant ToInstant(LocalDateTime localDateTime, DateTimeZone zone) => localDateTime.InZone(zone, Resolve).ToInstant();
}
