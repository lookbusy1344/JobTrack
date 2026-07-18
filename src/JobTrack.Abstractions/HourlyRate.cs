namespace JobTrack.Abstractions;

/// <summary>
///     A non-negative hourly labour rate in GBP (spec §9), at the same fixed <c>numeric(19,6)</c>
///     precision as <see cref="Money" />. Distinct from <see cref="Money" /> because a rate is always
///     "per hour" and the two are never interchangeable at a call site (ADR 0006's primitive-confusion
///     rationale extended to rate-vs-amount, not only identifiers).
/// </summary>
public readonly record struct HourlyRate
{
	/// <summary>Creates an <see cref="HourlyRate" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="amountPerHour" /> is negative.</exception>
	public HourlyRate(decimal amountPerHour) => AmountPerHour = amountPerHour >= 0m
		? amountPerHour
		: throw new ArgumentOutOfRangeException(nameof(amountPerHour), amountPerHour, "An hourly rate cannot be negative.");

	/// <summary>The rate, in GBP per hour.</summary>
	public decimal AmountPerHour { get; }
}
