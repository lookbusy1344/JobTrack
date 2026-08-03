namespace JobTrack.Domain.Costing;

using System.Globalization;
using System.Numerics;
using NodaTime;

/// <summary>
///     An exact aggregate of concurrency-allocated work-session duration. Internally retains the
///     rational tick quantity produced by <see cref="AllocatedShare" />; decimal hours are calculated
///     once, at the reporting boundary, rather than rounding each share before summation (ADR 0009).
/// </summary>
public sealed record AllocatedDuration : IFormattable
{
	private const int ReportingDecimalPlaces = 6;
	private const decimal ReportingScaleDecimal = 1_000_000m;
	private const string DefaultNumericFormat = "0.0";

	/// <summary>
	///     The hour count from which the tenth stops being rendered: three significant figures of hours
	///     already say more than the tenth does, and dropping it keeps the figure to a width a narrow
	///     table cell can hold.
	/// </summary>
	private const decimal WholeHoursThreshold = 100m;

	private const string WholeHoursNumericFormat = "0";
	private const string HoursSuffix = " hrs";

	private static readonly BigInteger ReportingScale = BigInteger.Pow(10, ReportingDecimalPlaces);
	private static readonly BigInteger TicksPerHour = Duration.FromHours(1).BclCompatibleTicks;

	private AllocatedDuration(BigInteger tickNumerator, BigInteger denominator)
	{
		var divisor = BigInteger.GreatestCommonDivisor(tickNumerator, denominator);
		TickNumerator = tickNumerator / divisor;
		Denominator = denominator / divisor;
	}

	private BigInteger TickNumerator { get; }

	private BigInteger Denominator { get; }

	/// <summary>No allocated work-session duration.</summary>
	public static AllocatedDuration Zero { get; } = new(BigInteger.Zero, BigInteger.One);

	/// <inheritdoc />
	/// <remarks>
	///     With no explicit <paramref name="format" /> the precision follows the figure's own magnitude:
	///     one decimal place below <see cref="WholeHoursThreshold" /> hours, whole hours at or above it.
	///     This is a rendering rule only — <see cref="ToHours" /> keeps its six decimal places either way.
	/// </remarks>
	public string ToString(string? format, IFormatProvider? formatProvider)
	{
		var hours = ToHours();
		return hours.ToString(format ?? DefaultNumericFormatFor(hours), formatProvider ?? CultureInfo.InvariantCulture) + HoursSuffix;
	}

	/// <summary>Creates an exact duration from one concurrency-allocated segment share.</summary>
	/// <exception cref="ArgumentException"><paramref name="share" /> is uninitialized.</exception>
	public static AllocatedDuration FromShare(AllocatedShare share)
	{
		if (share.IsUninitialized) {
			throw new ArgumentException("An allocated share must be initialized.", nameof(share));
		}

		return new(share.SegmentTicks, share.ConcurrencyDivisor);
	}

	/// <summary>Returns the exact sum of this duration and <paramref name="other" />.</summary>
	public AllocatedDuration Add(AllocatedDuration other)
	{
		ArgumentNullException.ThrowIfNull(other);
		var denominatorDivisor = BigInteger.GreatestCommonDivisor(Denominator, other.Denominator);
		var leftMultiplier = other.Denominator / denominatorDivisor;
		var rightMultiplier = Denominator / denominatorDivisor;
		return new(
			(TickNumerator * leftMultiplier) + (other.TickNumerator * rightMultiplier),
			Denominator * leftMultiplier);
	}

	/// <summary>
	///     Converts the exact duration to decimal hours at six decimal places using midpoint-to-even
	///     rounding. This is the duration reporting boundary; no earlier calculation rounds a share.
	/// </summary>
	public decimal ToHours()
	{
		var scaledNumerator = TickNumerator * ReportingScale;
		var hourDenominator = Denominator * TicksPerHour;
		var scaledHours = BigInteger.DivRem(scaledNumerator, hourDenominator, out var remainder);
		var midpointComparison = (remainder * 2).CompareTo(hourDenominator);
		if (midpointComparison > 0 || (midpointComparison == 0 && !scaledHours.IsEven)) {
			++scaledHours;
		}

		return (decimal)scaledHours / ReportingScaleDecimal;
	}

	/// <summary>
	///     Renders decimal hours to one decimal place, e.g. <c>3.5 hrs</c> or <c>3.0 hrs</c> — or, from
	///     100 hours up, as whole hours, e.g. <c>152 hrs</c>.
	/// </summary>
	public override string ToString() => ToString(null, null);

	private static string DefaultNumericFormatFor(decimal hours) =>
		hours >= WholeHoursThreshold ? WholeHoursNumericFormat : DefaultNumericFormat;
}
