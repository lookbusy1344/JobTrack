namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rule for deleting a leaf that has real <c>WorkSession</c> history (ADR 0036).
///     A node with children, prerequisite edges, or the permanent root is never deletable regardless of
///     role -- those are structural invariants checked separately, not an authorization question. This
///     policy governs only the one case ADR 0036 carves out: an administrator overriding the spec's
///     default "worked history is never physically deleted" rule for a single leaf.
/// </summary>
public static class JobNodeDeletePolicy
{
	/// <summary>
	///     An actor may force-delete a leaf with <c>WorkSession</c> history only if they hold
	///     <see cref="EmployeeRole.Administrator" />.
	/// </summary>
	public static bool CanForceDeleteWorkedLeaf(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator);
	}

	/// <summary>
	///     An actor may recursively delete or archive a whole subtree only if they hold
	///     <see cref="EmployeeRole.Administrator" /> (ADR 0061, superseding ADR 0036's blanket
	///     prohibition on subtree deletion). Structural refusals — the permanent root, an anchored
	///     request holding area — are invariants checked separately, not an authorization question.
	/// </summary>
	public static bool CanDeleteSubtree(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator);
	}
}
