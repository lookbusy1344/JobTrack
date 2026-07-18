namespace JobTrack.Domain.Tests.Costing;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;

public sealed class HierarchyDisplayReconcilerTests
{
	private static readonly JobNodeId ChildA = new(1);
	private static readonly JobNodeId ChildB = new(2);
	private static readonly JobNodeId ChildC = new(3);

	[Fact]
	public void When_naive_rounding_already_matches_the_parent_children_are_unchanged()
	{
		var children = new[] { (ChildA, new Money(5.00m)), (ChildB, new Money(5.00m)) };

		var result = HierarchyDisplayReconciler.Reconcile(new(10.00m), children);

		result.Should().BeEquivalentTo([
			new(ChildA, new(5.00m)),
			new ReconciledChildCost(ChildB, new(5.00m)),
		]);
	}

	[Fact]
	public void A_shortfall_is_applied_entirely_to_the_child_with_the_largest_under_rounding()
	{
		// Exact: 3.334 + 3.333 + 3.333 = 10.000, matching the exact parent total exactly.
		// Naive per-child rounding: 3.33 + 3.33 + 3.33 = 9.99, one penny short of the parent's 10.00.
		// Child A lost the most to rounding (0.004 vs 0.003), so it receives the missing penny.
		var children = new[] { (ChildA, new(3.334m)), (ChildB, new Money(3.333m)), (ChildC, new Money(3.333m)) };

		var result = HierarchyDisplayReconciler.Reconcile(new(10.000m), children);

		result.Should().BeEquivalentTo([
			new(ChildA, new(3.34m)),
			new(ChildB, new(3.33m)),
			new ReconciledChildCost(ChildC, new(3.33m)),
		]);
		result.Sum(child => child.DisplayedAmount.Amount).Should().Be(10.00m);
	}

	[Fact]
	public void A_surplus_is_applied_entirely_to_the_child_with_the_largest_over_rounding_breaking_ties_by_lowest_id()
	{
		// Exact: 3.335 + 3.335 + 3.35 = 10.02, matching the parent exactly.
		// Midpoint-to-even rounds each 3.335 up to 3.34 (even); naive sum 3.34+3.34+3.35 = 10.03,
		// one penny over the parent's 10.02. A and B are tied for largest over-rounding (both moved
		// 0.005 away from their exact value); the lower JobNodeId (A) absorbs the penny.
		var children = new[] { (ChildA, new(3.335m)), (ChildB, new Money(3.335m)), (ChildC, new Money(3.35m)) };

		var result = HierarchyDisplayReconciler.Reconcile(new(10.02m), children);

		result.Should().BeEquivalentTo([
			new(ChildA, new(3.33m)),
			new(ChildB, new(3.34m)),
			new ReconciledChildCost(ChildC, new(3.35m)),
		]);
		result.Sum(child => child.DisplayedAmount.Amount).Should().Be(10.02m);
	}

	[Fact]
	public void The_furthest_from_exact_child_is_selected_even_when_a_sibling_has_a_much_larger_rounded_amount()
	{
		// Child A's naive rounding moved 0.004 away from its exact value; Child B's moved only
		// 0.003 — A must absorb the shortfall penny purely on that basis. Child B's rounded amount
		// (100.00) dwarfs Child A's (1.00), so a selection rule that folded the rounded amount into
		// the ranking (rather than ranking on the rounding movement alone) would pick B instead.
		var children = new[] { (ChildA, new Money(1.004m)), (ChildB, new Money(100.003m)) };

		var result = HierarchyDisplayReconciler.Reconcile(new(101.007m), children);

		result.Should().BeEquivalentTo([
			new(ChildA, new(1.01m)),
			new ReconciledChildCost(ChildB, new(100.00m)),
		]);
	}

	[Fact]
	public void With_no_children_reconciliation_produces_no_result()
	{
		var result = HierarchyDisplayReconciler.Reconcile(new(10.00m), []);

		result.Should().BeEmpty();
	}

	[Theory]
	[InlineData(1.005, 1.005, 1.005, 3.015)]
	[InlineData(0.001, 0.001, 0.001, 0.003)]
	[InlineData(100.004, 200.006, 50.335, 350.345)]
	public void Displayed_children_always_sum_exactly_to_the_displayed_parent(decimal a, decimal b, decimal c, decimal exactParent)
	{
		var children = new[] { (ChildA, new(a)), (ChildB, new Money(b)), (ChildC, new Money(c)) };

		var result = HierarchyDisplayReconciler.Reconcile(new(exactParent), children);

		result.Sum(child => child.DisplayedAmount.Amount).Should().Be(new Money(exactParent).RoundToPennies().Amount);
	}
}
