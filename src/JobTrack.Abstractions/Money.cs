namespace JobTrack.Abstractions;

/// <summary>
///     A non-negative monetary amount in JobTrack's one installation-wide currency, Pounds Sterling
///     (spec §9). No <c>double</c>/<c>float</c> path exists for money anywhere in this project; every
///     amount is <see cref="decimal" /> at the fixed <c>numeric(19,6)</c> precision used by the schema.
///     Rounding to the GBP minor unit happens only at the reporting boundary (§10.4), never here.
/// </summary>
public readonly record struct Money
{
	/// <summary>The number of decimal places in the GBP minor unit (pennies).</summary>
	private const int GbpMinorUnitDecimalPlaces = 2;

	/// <summary>Creates a <see cref="Money" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="amount" /> is negative.</exception>
	public Money(decimal amount) => Amount = amount >= 0m
		? amount
		: throw new ArgumentOutOfRangeException(nameof(amount), amount, "Money cannot be negative.");

	/// <summary>The amount, in GBP.</summary>
	public decimal Amount { get; }

	/// <summary>
	///     Rounds to the nearest penny using midpoint-to-even (banker's) rounding — the reporting
	///     boundary of §10.4/ADR 0002/ADR 0009. Never applied to an intermediate cost-engine value.
	/// </summary>
	public Money RoundToPennies() => new(Math.Round(Amount, GbpMinorUnitDecimalPlaces, MidpointRounding.ToEven));
}
