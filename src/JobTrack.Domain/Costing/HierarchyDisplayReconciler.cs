namespace JobTrack.Domain.Costing;

using Abstractions;

/// <summary>
///     Reconciles one parent/children grouping's displayed penny amounts so they sum exactly to the
///     parent's displayed total (ADR 0002), applied independently at each hierarchy level. Every
///     child's exact amount is rounded to the nearest penny (midpoint-to-even); if the naive rounded
///     children do not sum to the parent's own rounded total, the entire residual is applied to the
///     single child whose naive rounding moved furthest from its exact value in the direction that
///     would cancel the residual, breaking ties by the lowest <see cref="JobNodeId" />. This is a
///     display concern only — the exact underlying values are never mutated.
/// </summary>
public static class HierarchyDisplayReconciler
{
	/// <summary>
	///     Reconciles <paramref name="children" />'s exact amounts against <paramref name="exactParentTotal" />.
	/// </summary>
	public static IReadOnlyList<ReconciledChildCost> Reconcile(Money exactParentTotal, IReadOnlyList<(JobNodeId ChildId, Money ExactAmount)> children)
	{
		if (children.Count == 0) {
			return [];
		}

		var naive = children
			.Select(child => (child.ChildId, child.ExactAmount, Rounded: child.ExactAmount.RoundToPennies()))
			.ToList();

		var parentPennies = exactParentTotal.RoundToPennies();
		var residual = parentPennies.Amount - naive.Sum(child => child.Rounded.Amount);
		if (residual == 0m) {
			return [.. naive.Select(child => new ReconciledChildCost(child.ChildId, child.Rounded))];
		}

		var direction = Math.Sign(residual);
		var adjustedChildId = naive
			.OrderByDescending(child => direction * (child.ExactAmount.Amount - child.Rounded.Amount))
			.ThenBy(child => child.ChildId.Value)
			.First()
			.ChildId;

		return [
			.. naive.Select(child => new ReconciledChildCost(
				child.ChildId,
				child.ChildId == adjustedChildId ? new(child.Rounded.Amount + residual) : child.Rounded)),
		];
	}
}
