namespace JobTrack.Domain.Schedules;

using Abstractions;
using NodaTime;

/// <summary>
///     One historical, effective-dated working-schedule version (spec §8.1): weekly intervals defined
///     in civil time, interpreted through <see cref="Zone" /> — the IANA zone snapshot recorded when
///     this version was current, retained even if the user's current zone later changes.
/// </summary>
public sealed record ScheduleVersion
{
	/// <summary>Creates a <see cref="ScheduleVersion" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="effectiveEnd" /> does not strictly follow <paramref name="effectiveStart" />.</exception>
	public ScheduleVersion(DateTimeZone zone, LocalDate effectiveStart, LocalDate? effectiveEnd, EquatableArray<WeeklyInterval> weeklyIntervals)
	{
		if (effectiveEnd is LocalDate end && end <= effectiveStart) {
			throw new ArgumentOutOfRangeException(nameof(effectiveEnd), effectiveEnd,
				"An effective end date must be strictly after the effective start date.");
		}

		Zone = zone;
		EffectiveStart = effectiveStart;
		EffectiveEnd = effectiveEnd;
		WeeklyIntervals = weeklyIntervals;
	}

	/// <summary>The IANA zone this version's civil-time weekly intervals are interpreted in.</summary>
	public DateTimeZone Zone { get; }

	/// <summary>The inclusive local date this version takes effect.</summary>
	public LocalDate EffectiveStart { get; }

	/// <summary>The exclusive local date this version stops applying, or <see langword="null" /> if still current.</summary>
	public LocalDate? EffectiveEnd { get; }

	/// <summary>The recurring civil-time working intervals in effect while this version applies.</summary>
	public EquatableArray<WeeklyInterval> WeeklyIntervals { get; }

	/// <summary>
	///     Value equality including <see cref="Zone" />, compared by its IANA id: <see cref="DateTimeZone" />
	///     does not itself override equality, but the TZDB provider caches one instance per id, so
	///     comparing ids is equivalent to comparing zones and gives this record correct value semantics.
	/// </summary>
	public bool Equals(ScheduleVersion? other) =>
		other is not null
		&& Zone.Id == other.Zone.Id
		&& EffectiveStart == other.EffectiveStart
		&& EffectiveEnd == other.EffectiveEnd
		&& WeeklyIntervals.Equals(other.WeeklyIntervals);

	/// <summary>Whether <paramref name="date" /> falls within <see cref="EffectiveStart" />/<see cref="EffectiveEnd" />.</summary>
	public bool IsEffectiveOn(LocalDate date) => date >= EffectiveStart && (EffectiveEnd is not LocalDate end || date < end);

	/// <inheritdoc cref="Equals(ScheduleVersion?)" />
	public override int GetHashCode() => HashCode.Combine(Zone.Id, EffectiveStart, EffectiveEnd, WeeklyIntervals);
}
