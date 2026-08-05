namespace JobTrack.AdminCli;

using System.Collections.Frozen;
using NodaTime;

/// <summary>
///     The day names <see cref="SetScheduleCommandOptions" />'s <c>--days</c> flag accepts: each
///     weekday's full and three-letter form, matched case-insensitively.
///
///     A fixed table rather than parsing through <see cref="DayOfWeek" /> or a culture-aware format,
///     so the accepted spellings are decided here and not by the host's current culture — the
///     container this runs in has no ICU at all.
///
///     It lives in its own static class rather than as a member of the options record because a
///     <see cref="FrozenDictionary{TKey,TValue}" /> has no value semantics, which a record's members
///     are required to have.
/// </summary>
internal static class ScheduleDayNames
{
	private static readonly FrozenDictionary<string, IsoDayOfWeek> DaysByName =
		new Dictionary<string, IsoDayOfWeek>(StringComparer.OrdinalIgnoreCase) {
			["monday"] = IsoDayOfWeek.Monday,
			["mon"] = IsoDayOfWeek.Monday,
			["tuesday"] = IsoDayOfWeek.Tuesday,
			["tue"] = IsoDayOfWeek.Tuesday,
			["wednesday"] = IsoDayOfWeek.Wednesday,
			["wed"] = IsoDayOfWeek.Wednesday,
			["thursday"] = IsoDayOfWeek.Thursday,
			["thu"] = IsoDayOfWeek.Thursday,
			["friday"] = IsoDayOfWeek.Friday,
			["fri"] = IsoDayOfWeek.Friday,
			["saturday"] = IsoDayOfWeek.Saturday,
			["sat"] = IsoDayOfWeek.Saturday,
			["sunday"] = IsoDayOfWeek.Sunday,
			["sun"] = IsoDayOfWeek.Sunday,
		}.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

	/// <summary>Resolves one day name, returning <see langword="false" /> if it is not recognized.</summary>
	public static bool TryParse(string name, out IsoDayOfWeek day) => DaysByName.TryGetValue(name, out day);
}
