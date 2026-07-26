namespace JobTrack.Abstractions;

/// <summary>
///     A non-negative hourly labour rate in GBP (spec §9), at the same fixed <c>numeric(19,6)</c>
///     precision as <see cref="Money" />. Distinct from <see cref="Money" /> because a rate is always
///     "per hour" and the two are never interchangeable at a call site (ADR 0006's primitive-confusion
///     rationale extended to rate-vs-amount, not only identifiers).
/// </summary>
public readonly record struct HourlyRate : IComparable<HourlyRate>, IFormattable
{
	/// <summary>The suffix marking a rendered rate as per-hour, distinguishing it from a bare <see cref="Money" />.</summary>
	private const string PerHourSuffix = "/hr";

	/// <summary>Creates an <see cref="HourlyRate" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="amountPerHour" /> is negative.</exception>
	public HourlyRate(decimal amountPerHour) => AmountPerHour = amountPerHour >= 0m
		? amountPerHour
		: throw new ArgumentOutOfRangeException(nameof(amountPerHour), amountPerHour, "An hourly rate cannot be negative.");

	/// <summary>The rate, in GBP per hour.</summary>
	public decimal AmountPerHour { get; }

	/// <inheritdoc />
	public int CompareTo(HourlyRate other) => AmountPerHour.CompareTo(other.AmountPerHour);

	/// <inheritdoc />
	public string ToString(string? format, IFormatProvider? formatProvider) =>
		SterlingFormat.Format(AmountPerHour, format, formatProvider) + PerHourSuffix;

	/// <summary>Renders the rate as Sterling per hour, e.g. <c>£18.50/hr</c>.</summary>
	public override string ToString() => ToString(null, null);

	/// <summary>Whether <paramref name="left" /> is less than <paramref name="right" />.</summary>
	public static bool operator <(HourlyRate left, HourlyRate right) => left.CompareTo(right) < 0;

	/// <summary>Whether <paramref name="left" /> is greater than <paramref name="right" />.</summary>
	public static bool operator >(HourlyRate left, HourlyRate right) => left.CompareTo(right) > 0;

	/// <summary>Whether <paramref name="left" /> is less than or equal to <paramref name="right" />.</summary>
	public static bool operator <=(HourlyRate left, HourlyRate right) => left.CompareTo(right) <= 0;

	/// <summary>Whether <paramref name="left" /> is greater than or equal to <paramref name="right" />.</summary>
	public static bool operator >=(HourlyRate left, HourlyRate right) => left.CompareTo(right) >= 0;
}
