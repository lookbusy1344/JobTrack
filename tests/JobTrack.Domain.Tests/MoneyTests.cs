namespace JobTrack.Domain.Tests;

using Abstractions;
using AwesomeAssertions;

/// <summary>
///     <see cref="Money" /> is a §7.1 public value type (JobTrack.Abstractions), exercised here per
///     this project's Domain unit-test category (docs/traceability/test-catalogue.md §2) since it is a
///     domain value object in every practical sense even though ADR 0006/CLAUDE.md house it in
///     Abstractions alongside the identifiers.
/// </summary>
public sealed class MoneyTests
{
	[Fact]
	public void A_non_negative_amount_is_accepted()
	{
		var money = new Money(12.34m);

		money.Amount.Should().Be(12.34m);
	}

	[Fact]
	public void Zero_is_accepted()
	{
		var money = new Money(0m);

		money.Amount.Should().Be(0m);
	}

	[Fact]
	public void A_negative_amount_is_rejected()
	{
		var act = () => new Money(-0.01m);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Two_instances_with_the_same_amount_are_equal() => new Money(5m).Should().Be(new Money(5m));

	[Fact]
	public void Rounding_to_pennies_rounds_a_non_midpoint_value_normally() => new Money(1.006m).RoundToPennies().Should().Be(new Money(1.01m));

	[Theory]
	[InlineData(0.005, 0.00)]
	[InlineData(0.015, 0.02)]
	[InlineData(0.025, 0.02)]
	public void Rounding_to_pennies_breaks_a_midpoint_to_the_nearest_even_penny(double amount, double expected) =>
		new Money((decimal)amount).RoundToPennies().Should().Be(new Money((decimal)expected));
}
