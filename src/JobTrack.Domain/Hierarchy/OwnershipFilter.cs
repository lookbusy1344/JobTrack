namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     Distinguishes "no owner filter" from "only unassigned" from "only this owner" for job-tree
///     browsing/search/awaiting-progress queries (ownership model §2.1/§4.3) — a plain
///     <c>AppUserId?</c> cannot express both "no filter" and "only unassigned" now that
///     <c>owner_user_id</c> is itself nullable. Construct via <see cref="All" />, <see cref="Unassigned" />,
///     or <see cref="OwnedBy" />; the private constructor keeps <see cref="OwnerUserId" /> non-null if and
///     only if <see cref="Kind" /> is <see cref="OwnershipFilterKind.OwnedBy" />.
/// </summary>
public sealed record OwnershipFilter
{
	/// <summary>Every node matches, regardless of owner.</summary>
	public static readonly OwnershipFilter All = new() { Kind = OwnershipFilterKind.All };

	/// <summary>Only nodes with no direct owner (the pickup pool).</summary>
	public static readonly OwnershipFilter Unassigned = new() { Kind = OwnershipFilterKind.Unassigned };

	private OwnershipFilter()
	{
	}

	/// <summary>Which of the three filter shapes this is.</summary>
	public required OwnershipFilterKind Kind { get; init; }

	/// <summary>The owner to match. Non-null if and only if <see cref="Kind" /> is <see cref="OwnershipFilterKind.OwnedBy" />.</summary>
	public AppUserId? OwnerUserId { get; init; }

	/// <summary>Only nodes directly owned by <paramref name="ownerUserId" />.</summary>
	public static OwnershipFilter OwnedBy(AppUserId ownerUserId) => new() { Kind = OwnershipFilterKind.OwnedBy, OwnerUserId = ownerUserId };

	/// <summary>Whether a node whose direct owner is <paramref name="actualOwnerUserId" /> (null for unassigned) matches this filter.</summary>
	public bool Matches(AppUserId? actualOwnerUserId) =>
		Kind switch {
			OwnershipFilterKind.All => true,
			OwnershipFilterKind.Unassigned => actualOwnerUserId is null,
			OwnershipFilterKind.OwnedBy => actualOwnerUserId == OwnerUserId,
			_ => throw new InvalidOperationException($"Unrecognised ownership filter kind: {Kind}."),
		};
}
