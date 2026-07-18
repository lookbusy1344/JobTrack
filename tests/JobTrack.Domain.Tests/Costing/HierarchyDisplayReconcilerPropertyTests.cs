namespace JobTrack.Domain.Tests.Costing;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using FsCheck;
using FsCheck.Xunit;

/// <summary>
///     The property test ADR 0002 requires: for generated hierarchies of varying breadth and generated
///     cost values, the displayed (penny-rounded) child amounts always sum exactly to the displayed
///     parent amount. <see cref="HierarchyDisplayReconcilerTests" /> covers the golden/tie-break cases;
///     this file is the exact-conservation property across randomised inputs, including adversarial
///     cases where several children round in the same direction.
/// </summary>
public sealed class HierarchyDisplayReconcilerPropertyTests
{
	[Property]
	public void Displayed_children_always_sum_exactly_to_the_displayed_parent(PositiveInt[] childMicroPoundAmounts)
	{
		if (childMicroPoundAmounts.Length == 0) {
			return;
		}

		// Values are constructed to numeric(19,6) micro-pound granularity (ADR 0009), matching the
		// precision the real cost engine carries before the reporting boundary.
		var children = childMicroPoundAmounts
			.Select((value, index) => (ChildId: new JobNodeId(index + 1), ExactAmount: new Money(value.Get / 1_000_000m)))
			.ToArray();

		// The exact parent total is always the sum of its children's exact amounts (spec §10.4) --
		// never an independently chosen value -- so the property mirrors HierarchicalCostAggregator's
		// own invariant rather than an arbitrary, potentially inconsistent parent/children pairing.
		var exactParent = new Money(children.Sum(child => child.ExactAmount.Amount));

		var result = HierarchyDisplayReconciler.Reconcile(exactParent, children);

		result.Sum(child => child.DisplayedAmount.Amount).Should().Be(exactParent.RoundToPennies().Amount);
	}
}
