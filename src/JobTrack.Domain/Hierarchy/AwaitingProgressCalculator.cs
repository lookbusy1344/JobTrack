namespace JobTrack.Domain.Hierarchy;

using Abstractions;
using NodaTime;

/// <summary>
///     Derives the flat "jobs awaiting progress" list (plan §8.5): leaves only — never a branch or the
///     root — that have not reached a terminal achievement (<see cref="Achievement.Success" />,
///     <see cref="Achievement.Cancelled" />, or <see cref="Achievement.Unsuccessful" />) and are not
///     archived. A leaf with no <c>LeafWork</c> attached yet (<see cref="HierarchyNode.LeafAchievement" />
///     is <see langword="null" />) is included: it still needs someone to attach work or decompose it
///     further, so it is exactly as actionable as a <see cref="Achievement.Waiting" /> leaf, not
///     invisible to the queue. A leaf blocked by an unsatisfied prerequisite (see
///     <see cref="ReadinessCalculator" />) stays on the list too, rather than disappearing — someone
///     still needs to be aware of it — but carries <see cref="AwaitingProgressEntry.IsReady" /> so the
///     caller can surface it as blocked instead of actionable. Optionally scoped to one owner and/or
///     one subtree, ordered by descending <see cref="Priority" /> then ascending deadline
///     (<see cref="AwaitingProgressNodeFacts.NeededFinish" />, falling back to
///     <see cref="AwaitingProgressNodeFacts.NeededStart" />), nulls last.
/// </summary>
public static class AwaitingProgressCalculator
{
	/// <summary>
	///     Filters and orders <paramref name="nodesById" /> into the awaiting-progress list. Both
	///     dictionaries must be keyed by the same complete node set; <paramref name="factsById" /> is
	///     looked up only for candidate leaves. <paramref name="searchText" />, when non-blank, restricts
	///     to leaves whose <see cref="AwaitingProgressNodeFacts.Description" /> contains it (case
	///     insensitive) — unlike <c>IJobQueries.SearchJobNodesAsync</c>, this scopes the dashboard's own
	///     owner/subtree-filtered candidate set rather than the whole tree.
	/// </summary>
	public static EquatableArray<AwaitingProgressEntry> GetAwaitingProgress(
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyDictionary<JobNodeId, AwaitingProgressNodeFacts> factsById,
		IReadOnlyCollection<PrerequisiteEdge> prerequisites,
		OwnershipFilter ownership,
		JobNodeId? subtreeRootId,
		string? searchText = null)
	{
		ArgumentNullException.ThrowIfNull(ownership);

		var candidates = nodesById.Values.Where(IsUnfinishedLeaf);

		var entries = candidates
			.Select(node => (Node: node, Facts: factsById[node.Id]))
			.Where(candidate => candidate.Facts.ArchivedAt is null)
			.Where(candidate => ownership.Matches(candidate.Facts.OwnerUserId))
			.Where(candidate => !subtreeRootId.HasValue || IsInSubtree(candidate.Node.Id, subtreeRootId.Value, nodesById))
			.Where(candidate => string.IsNullOrWhiteSpace(searchText)
								|| candidate.Facts.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase))
			.Select(candidate => new AwaitingProgressEntry(
				candidate.Node.Id,
				candidate.Node.ParentId,
				candidate.Facts.Description,
				candidate.Facts.OwnerUserId,
				candidate.Facts.Priority,
				candidate.Node.LeafAchievement,
				null,
				candidate.Facts.NeededStart,
				candidate.Facts.NeededFinish,
				ReadinessCalculator.IsReady(candidate.Node.Id, nodesById, prerequisites).IsReady));

		var ordered = entries
			.OrderByDescending(entry => entry.Priority)
			.ThenBy(entry => Deadline(entry) is null)
			.ThenBy(entry => Deadline(entry))
			.ThenBy(entry => entry.Id.Value);

		return [.. ordered];
	}

	private static Instant? Deadline(AwaitingProgressEntry entry) => entry.NeededFinish ?? entry.NeededStart;

	/// <summary>
	///     A leaf (childless, non-root node) that has not reached a terminal achievement — this
	///     includes a leaf with no <c>LeafWork</c> attached at all, per the type's own remarks.
	/// </summary>
	private static bool IsUnfinishedLeaf(HierarchyNode node) =>
		node.ParentId is not null && node.ChildIds.Count == 0 && node.LeafAchievement is null or Achievement.Waiting or Achievement.InProgress;

	private static bool IsInSubtree(JobNodeId id, JobNodeId rootId, IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById)
	{
		JobNodeId? current = id;
		while (current is JobNodeId currentId) {
			if (currentId == rootId) {
				return true;
			}

			current = HierarchyNodeLookup.GetRequired(nodesById, currentId).ParentId;
		}

		return false;
	}
}
