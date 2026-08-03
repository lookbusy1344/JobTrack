namespace JobTrack.Abstractions;

/// <summary>
///     A non-negative monetary amount in JobTrack's one installation-wide currency, Pounds Sterling
///     (spec §9). No <c>double</c>/<c>float</c> path exists for money anywhere in this project; every
///     amount is <see cref="decimal" /> at the fixed <c>numeric(19,6)</c> precision used by the schema.
///     Rounding to the GBP minor unit happens only at the reporting boundary (§10.4), never here.
/// </summary>
public readonly record struct Money : IComparable<Money>, IFormattable
{
	/// <summary>The number of decimal places in the GBP minor unit (pennies).</summary>
	internal const int GbpMinorUnitDecimalPlaces = 2;

	/// <summary>Creates a <see cref="Money" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="amount" /> is negative.</exception>
	public Money(decimal amount) => Amount = amount >= 0m
		? amount
		: throw new ArgumentOutOfRangeException(nameof(amount), amount, "Money cannot be negative.");

	/// <summary>The amount, in GBP.</summary>
	public decimal Amount { get; }

	/// <inheritdoc />
	public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

	/// <inheritdoc />
	/// <remarks>
	///     With no explicit <paramref name="format" /> the precision follows the amount's own magnitude:
	///     pennies below &#163;1,000, whole pounds at or above it, where the two minor-unit digits add
	///     nothing to four significant figures of pounds. This is a rendering rule only — it never
	///     changes the amount, and <see cref="Amount" /> keeps the full <c>numeric(19,6)</c> value.
	/// </remarks>
	public string ToString(string? format, IFormatProvider? formatProvider) =>
		SterlingFormat.FormatByMagnitude(Amount, format, formatProvider);

	/// <summary>
	///     Rounds to the nearest penny using midpoint-to-even (banker's) rounding — the reporting
	///     boundary of §10.4/ADR 0002/ADR 0009. Never applied to an intermediate cost-engine value.
	/// </summary>
	public Money RoundToPennies() => new(Math.Round(Amount, GbpMinorUnitDecimalPlaces, MidpointRounding.ToEven));

	/// <summary>Renders the amount as Sterling, e.g. <c>£234.50</c> or, from £1,000 up, <c>£1,235</c>.</summary>
	public override string ToString() => ToString(null, null);

	/// <summary>Whether <paramref name="left" /> is less than <paramref name="right" />.</summary>
	public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

	/// <summary>Whether <paramref name="left" /> is greater than <paramref name="right" />.</summary>
	public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

	/// <summary>Whether <paramref name="left" /> is less than or equal to <paramref name="right" />.</summary>
	public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

	/// <summary>Whether <paramref name="left" /> is greater than or equal to <paramref name="right" />.</summary>
	public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;
}
