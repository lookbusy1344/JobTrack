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
	///     The amount from which the pennies stop being rendered. Four significant figures of pounds
	///     already say more than the two minor-unit digits do, and dropping them keeps a column of totals
	///     to a width a narrow table cell can hold.
	/// </summary>
	private const decimal WholePoundsThreshold = 1_000m;

	/// <summary>Group-separated whole pounds — the default for an amount at or above <see cref="WholePoundsThreshold" />.</summary>
	private const string WholePoundsNumericFormat = "N0";

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

	/// <summary>
	///     Renders <paramref name="amount" /> as a Sterling amount whose default precision follows its
	///     magnitude: pennies below <see cref="WholePoundsThreshold" />, whole pounds at or above it. An
	///     explicit <paramref name="format" /> always wins, so a caller that needs the pennies can still
	///     ask for them.
	/// </summary>
	internal static string FormatByMagnitude(decimal amount, string? format, IFormatProvider? formatProvider) =>
		Format(amount, format ?? DefaultNumericFormatFor(amount), formatProvider);

	private static string DefaultNumericFormatFor(decimal amount) =>
		amount >= WholePoundsThreshold ? WholePoundsNumericFormat : DefaultNumericFormat;
}
