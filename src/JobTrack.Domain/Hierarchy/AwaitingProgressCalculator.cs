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
///     caller can surface it as blocked instead of actionable. Ordered by readiness first (every ready
///     leaf before every blocked one — nothing can be done about a blocked leaf, so it sinks below the
///     actionable queue), then by descending <see cref="Priority" />, then ascending deadline
///     (<see cref="AwaitingProgressNodeFacts.NeededFinish" />, falling back to
///     <see cref="AwaitingProgressNodeFacts.NeededStart" />), nulls last.
/// </summary>
/// <remarks>
///     2026-07-25 scalability-follow-up plan §2.1: ownership, subtree-root, search-text, and
///     offset/limit scoping now happen in <c>IAwaitingProgressQueryPort</c>'s own query (the port
///     receives an <c>AwaitingProgressQueryFilter</c> and returns only the already-filtered,
///     already-paged candidate page plus the ancestor/required-job facts readiness needs) — this
///     calculator is the pure authority for readiness and output mapping only, and must not reapply
///     any of those request-scoped filters against its own node dictionary, which is deliberately
///     narrowed to the requested page, not the whole installation's unfinished leaves.
/// </remarks>
public static class AwaitingProgressCalculator
{
	/// <summary>
	///     Filters and orders a complete hierarchy snapshot into the awaiting-progress list.
	///     Retained for compatibility with consumers that perform ownership, subtree, and search
	///     filtering in the functional core.
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

		var candidates = nodesById.Values
			.Where(IsUnfinishedLeaf)
			.Where(node => ownership.Matches(factsById[node.Id].OwnerUserId))
			.Where(node => !subtreeRootId.HasValue || IsInSubtree(node.Id, subtreeRootId.Value, nodesById))
			.Where(node => string.IsNullOrWhiteSpace(searchText)
						   || factsById[node.Id].Description.Contains(searchText, StringComparison.OrdinalIgnoreCase));

		return MapCandidates(candidates, nodesById, factsById, prerequisites);
	}

	/// <summary>
	///     Maps the already-filtered, already-paged candidate set in <paramref name="nodesById" /> into
	///     the awaiting-progress list, re-deriving that set by its shape (childless, non-terminal
	///     achievement) to distinguish genuine candidates from the ancestor/required-job waypoints the
	///     port also includes for <see cref="ReadinessCalculator" />'s own use. Both dictionaries must be
	///     keyed by the same complete node set; <paramref name="factsById" /> is looked up only for
	///     candidate leaves.
	/// </summary>
	public static EquatableArray<AwaitingProgressEntry> GetAwaitingProgress(
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyDictionary<JobNodeId, AwaitingProgressNodeFacts> factsById,
		IReadOnlyCollection<PrerequisiteEdge> prerequisites)
	{
		var candidates = nodesById.Values.Where(IsUnfinishedLeaf);

		return MapCandidates(candidates, nodesById, factsById, prerequisites);
	}

	private static EquatableArray<AwaitingProgressEntry> MapCandidates(
		IEnumerable<HierarchyNode> candidates,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyDictionary<JobNodeId, AwaitingProgressNodeFacts> factsById,
		IReadOnlyCollection<PrerequisiteEdge> prerequisites)
	{
		var entries = candidates
			.Select(node => (Node: node, Facts: factsById[node.Id]))
			.Where(candidate => candidate.Facts.ArchivedAt is null)
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
			.OrderByDescending(entry => entry.IsReady)
			.ThenByDescending(entry => entry.Priority)
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
