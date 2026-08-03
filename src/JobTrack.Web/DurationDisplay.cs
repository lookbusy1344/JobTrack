namespace JobTrack.Web;

using System.Globalization;
using NodaTime;

/// <summary>
///     Shared formatting for an elapsed wall-clock <see cref="Duration" /> — how long two jobs' work
///     sessions overlapped, not an allocated or costed figure (<see cref="Application.CostDetailsResult" />'s
///     <c>AllocatedDuration</c> renders through its own <c>ToString</c>). Whole minutes, since a session
///     boundary is entered to the minute: "3h 20m", "45m", and "0m" for an interval shorter than a
///     minute rather than a spurious "0h".
/// </summary>
internal static class DurationDisplay
{
	private const int MinutesPerHour = 60;

	internal static string Format(Duration duration)
	{
		var totalMinutes = (long)duration.TotalMinutes;
		var hours = totalMinutes / MinutesPerHour;
		var minutes = totalMinutes % MinutesPerHour;

		return hours == 0
			? string.Create(CultureInfo.InvariantCulture, $"{minutes}m")
			: string.Create(CultureInfo.InvariantCulture, $"{hours}h {minutes}m");
	}
}
