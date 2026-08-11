namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Builds <see cref="JobNodeResult" /> read models from structural facts (<c>parent_id</c>, child
///     existence, <c>leaf_work</c> existence) rather than stored kind columns (ADR 0035).
/// </summary>
internal static class JobNodeStructuralResults
{
	/// <summary>
	///     Classifies a node from structure only — matches <see cref="Domain.Hierarchy.NodeClassifier" />
	///     rules without considering <c>leaf_work</c>.
	/// </summary>
	public static NodeKind DeriveKind(JobNodeId? parentId, bool hasChildren)
	{
		if (parentId is null) {
			return NodeKind.Root;
		}

		if (hasChildren) {
			return NodeKind.Branch;
		}

		return NodeKind.Leaf;
	}

	/// <summary>Projects a loaded node when structural facts are already known.</summary>
	public static JobNodeResult ToResult(
		JobNodeId id,
		JobNodeId? parentId,
		string description,
		string? writeUp,
		AppUserId postedByUserId,
		AppUserId? ownerUserId,
		decimal? expectedDurationHours,
		Money? expectedCost,
		Instant? neededStart,
		Instant? neededFinish,
		Priority priority,
		Instant postedAt,
		Instant? archivedAt,
		long version,
		bool hasChildren,
		bool hasLeafWork) => new() {
			Id = id,
			ParentId = parentId,
			Kind = DeriveKind(parentId, hasChildren),
			Description = description,
			WriteUp = writeUp,
			PostedByUserId = postedByUserId,
			OwnerUserId = ownerUserId,
			ExpectedDurationHours = expectedDurationHours,
			ExpectedCost = expectedCost,
			NeededStart = neededStart,
			NeededFinish = neededFinish,
			Priority = priority,
			PostedAt = postedAt,
			ArchivedAt = archivedAt,
			Version = version,
			HasChildren = hasChildren,
			HasLeafWork = hasLeafWork,
		};
}
