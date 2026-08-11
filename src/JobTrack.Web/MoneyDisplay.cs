namespace JobTrack.Web;

using Abstractions;

/// <summary>
///     Shared formatting for user-facing money values across the web host. Costs are shown as GBP with
///     a currency symbol and two decimal places everywhere they are rendered in HTML.
/// </summary>
/// <remarks>
///     The rendering itself now lives on <see cref="Money" /> so that every consumer of the library —
///     not only this host — gets the same text, and so an interpolated <c>$"{money}"</c> cannot leak
///     the compiler-generated record form. This wrapper is kept because the Razor pages name it, and
///     it is the seam at which a future host-specific presentation choice would go.
/// </remarks>
internal static class MoneyDisplay
{
	/// <summary>Stands in for a zero cost so it doesn't read as "nothing recorded".</summary>
	private const string ZeroCostPlaceholder = "-";

	internal static string Format(Money money) => money.Amount == 0m ? ZeroCostPlaceholder : money.ToString();
}
