namespace JobTrack.Abstractions;

using System.Globalization;

/// <summary>
///     The one place JobTrack's single installation-wide currency (Pounds Sterling, spec §9) is
///     rendered as text, shared by <see cref="Money" /> and <see cref="HourlyRate" /> so the symbol and
///     precision are declared once rather than restated at each formatting site.
/// </summary>
/// <remarks>
///     The deployed runtime image runs in ICU-less globalization-invariant mode, where
///     <c>CultureInfo.GetCultureInfo("en-GB")</c> throws <see cref="CultureNotFoundException" />, so the
///     standard <c>"C"</c> currency specifier is unavailable. Sterling is the only currency this
///     product renders, so the symbol is a literal and the amount is formatted against
///     <see cref="CultureInfo.InvariantCulture" />.
/// </remarks>
internal static class SterlingFormat
{
	/// <summary>The Pound Sterling currency symbol.</summary>
	internal const string Symbol = "£";

	/// <summary>
	///     Group-separated fixed-point at the GBP minor unit — the default when a caller supplies no
	///     format of its own. Derived from <see cref="Money.GbpMinorUnitDecimalPlaces" /> so the precision
	///     is stated once.
	/// </summary>
	internal static readonly string DefaultNumericFormat =
		"N" + Money.GbpMinorUnitDecimalPlaces.ToString(CultureInfo.InvariantCulture);

	/// <summary>Renders <paramref name="amount" /> as a Sterling amount.</summary>
	internal static string Format(decimal amount, string? format, IFormatProvider? formatProvider) =>
		Symbol + amount.ToString(format ?? DefaultNumericFormat, formatProvider ?? CultureInfo.InvariantCulture);
}
