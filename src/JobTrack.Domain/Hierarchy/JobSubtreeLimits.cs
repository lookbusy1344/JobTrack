namespace JobTrack.Domain.Hierarchy;

/// <summary>
///     Contract-level bounds for a Browse multi-level subtree fetch (ADR 0039), enforced by the
///     persistence query, not just the web default.
/// </summary>
public static class JobSubtreeLimits
{
	/// <summary>Default render depth below the requested root when a caller specifies none.</summary>
	public const int DefaultMaxDepth = 3;

	/// <summary>The largest depth a caller may request; a greater value is rejected, not clamped.</summary>
	public const int HardMaxDepth = 5;

	/// <summary>
	///     Immediate children of the subtree root are never capped. For every parent whose children are
	///     expanded to a further level (level 1 onward), only the first this-many children (by <c>Id</c>
	///     order) have their own descendants fetched; the rest still render but do not recurse further.
	/// </summary>
	public const int BreadthCap = 25;
}
