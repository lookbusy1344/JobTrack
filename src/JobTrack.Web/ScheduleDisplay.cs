namespace JobTrack.Web;

using NodaTime;
using NodaTime.Text;

/// <summary>Shared formatting for schedule/rota dates across the web host.</summary>
internal static class ScheduleDisplay
{
	// CreateWithInvariantCulture, matching InstantDisplay/MoneyDisplay: the runtime image runs in
	// ICU-less globalization-invariant mode (see Dockerfile), where a named culture's day/month
	// names would throw -- and even under a named culture, the exact punctuation of a "full date"
	// pattern varies by host ICU/CLDR version, so an explicit pattern is the only way to keep this
	// deterministic across environments.
	private static readonly LocalDatePattern Pattern = LocalDatePattern.CreateWithInvariantCulture("dddd, d MMMM yyyy");

	internal static string Format(LocalDate date) => Pattern.Format(date);
}
