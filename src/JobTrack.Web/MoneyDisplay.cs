namespace JobTrack.Web;

using System.Globalization;
using Abstractions;

/// <summary>
///     Shared formatting for user-facing money values across the web host. Costs are shown as GBP with
///     a currency symbol and two decimal places everywhere they are rendered in HTML.
/// </summary>
internal static class MoneyDisplay
{
	private const string SterlingSymbol = "£";

	// The runtime image runs in ICU-less globalization-invariant mode (see Dockerfile), where
	// CultureInfo.GetCultureInfo("en-GB") throws CultureNotFoundException. Sterling is the only
	// currency this app renders, so the symbol is hardcoded and formatted with InvariantCulture
	// rather than depending on ICU culture data.
	internal static string Format(Money money) => SterlingSymbol + money.Amount.ToString("N2", CultureInfo.InvariantCulture);
}
