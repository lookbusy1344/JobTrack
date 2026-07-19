namespace JobTrack.Application;

using Abstractions;
using Domain.Hierarchy;
using NodaTime;

/// <summary>
///     Pure validation and ordering logic for <see cref="IJobCommands.ImportSubtreeAsync" /> (no I/O, no
///     port calls): turns a flat, request-local-id-keyed <see cref="ImportSubtreeNodeSpec" /> batch into
///     a parents-before-children creation order, rejecting an empty batch, a duplicate local id, a
///     dangling parent/prerequisite reference, or a parent-reference cycle before any database write is
///     attempted.
/// </summary>
internal static class SubtreeImportPlanner
{
	/// <summary>
	///     Orders <paramref name="nodes" /> so that every node appears after its request-local
	///     parent (a <see langword="null" /> <see cref="ImportSubtreeNodeSpec.ParentLocalId" /> is a root,
	///     ordered first).
	/// </summary>
	/// <exception cref="InvariantViolationException">
	///     The batch is empty, has a duplicate local id, references an unknown parent or prerequisite
	///     local id, or its parent references form a cycle.
	/// </exception>
	public static EquatableArray<ImportSubtreeNodeSpec> BuildCreationOrder(EquatableArray<ImportSubtreeNodeSpec> nodes)
	{
		if (nodes.Count == 0) {
			throw new InvariantViolationException("import-subtree-empty", "The import batch contains no nodes.");
		}

		var byLocalId = new Dictionary<long, ImportSubtreeNodeSpec>(nodes.Count);
		foreach (var node in nodes) {
			if (!byLocalId.TryAdd(node.LocalId, node)) {
				throw new InvariantViolationException(
					"import-subtree-duplicate-local-id", $"Duplicate node local id {node.LocalId} in the import batch.");
			}
		}

		foreach (var node in nodes) {
			if (node.ParentLocalId is long parentLocalId && !byLocalId.ContainsKey(parentLocalId)) {
				throw new InvariantViolationException(
					"import-subtree-unknown-parent-local-id",
					$"Node {node.LocalId} references unknown parent local id {parentLocalId}.");
			}

			foreach (var prerequisiteLocalId in node.PrerequisiteLocalIds) {
				if (!byLocalId.ContainsKey(prerequisiteLocalId)) {
					throw new InvariantViolationException(
						"import-subtree-unknown-prerequisite-local-id",
						$"Node {node.LocalId} references unknown prerequisite local id {prerequisiteLocalId}.");
				}
			}
		}

		var childrenByParent = nodes
			.Where(n => n.ParentLocalId is not null)
			.GroupBy(n => n.ParentLocalId!.Value)
			.ToDictionary(g => g.Key, g => (IReadOnlyList<ImportSubtreeNodeSpec>)[.. g]);

		var ordered = new List<ImportSubtreeNodeSpec>(nodes.Count);
		var visited = new HashSet<long>(nodes.Count);
		var frontier = new Queue<ImportSubtreeNodeSpec>(nodes.Where(n => n.ParentLocalId is null));

		while (frontier.Count > 0) {
			var node = frontier.Dequeue();
			if (!visited.Add(node.LocalId)) {
				continue;
			}

			ordered.Add(node);

			if (childrenByParent.TryGetValue(node.LocalId, out var children)) {
				foreach (var child in children) {
					frontier.Enqueue(child);
				}
			}
		}

		if (ordered.Count != nodes.Count) {
			throw new InvariantViolationException(
				"import-subtree-parent-cycle", "The import batch's parent references form a cycle.");
		}

		ValidateLeafWork(ordered, childrenByParent);

		return [.. ordered];
	}

	/// <summary>
	///     Rejects any <see cref="ImportSubtreeNodeSpec.LeafWork" /> the batch could not produce as a
	///     real history, before a single row is written: work recorded against a node that is a parent
	///     within the batch, an achievement that contradicts having worked at all, a session that
	///     finishes before it starts, a leaf closed while its only session is still open, and — the
	///     prerequisite gate (spec §6) evaluated over the batch's own edges — work on a leaf whose
	///     prerequisites never reach <see cref="Achievement.Success" />, or which starts before those
	///     prerequisites finished.
	///     <para>
	///         The last check is what the runtime gate alone cannot give. Replaying the batch in
	///         prerequisite order would let every dependent pass <see cref="Domain.Hierarchy.ReadinessCalculator" />
	///         no matter what instants it carries, so a caller could record a job as starting a day
	///         before the job it depends on finished. Both providers re-check readiness against real
	///         database state as well; this check is the one that makes the recorded <em>timeline</em>
	///         consistent, not merely the end state.
	///     </para>
	/// </summary>
	/// <param name="ordered">The batch in parents-before-children order.</param>
	/// <param name="childrenByParent">The batch's children indexed by parent local id.</param>
	private static void ValidateLeafWork(
		IReadOnlyList<ImportSubtreeNodeSpec> ordered,
		IReadOnlyDictionary<long, IReadOnlyList<ImportSubtreeNodeSpec>> childrenByParent)
	{
		var worked = ordered.Where(n => n.LeafWork is not null).ToList();
		if (worked.Count == 0) {
			return;
		}

		foreach (var node in worked) {
			ValidateWorkShape(node, node.LeafWork!, childrenByParent);
		}

		var byLocalId = ordered.ToDictionary(n => n.LocalId);
		var (achieved, latestFinish) = DeriveSubtreeOutcomes(ordered, childrenByParent);

		foreach (var node in worked) {
			foreach (var requiredLocalId in EffectivePrerequisiteLocalIds(node, byLocalId)) {
				if (!achieved[requiredLocalId]) {
					throw new InvariantViolationException(
						"import-subtree-work-blocked-by-prerequisite",
						$"Node {node.LocalId} records work, but its prerequisite {requiredLocalId} does not succeed in this batch.");
				}

				// An achieved prerequisite is necessarily closed, so its subtree has a finish instant.
				var requiredFinish = latestFinish[requiredLocalId]!.Value;
				if (node.LeafWork!.StartedAt < requiredFinish) {
					throw new InvariantViolationException(
						"import-subtree-work-precedes-prerequisite",
						$"Node {node.LocalId}'s work starts at {node.LeafWork.StartedAt}, before its prerequisite "
						+ $"{requiredLocalId} finished at {requiredFinish}.");
				}
			}
		}
	}

	/// <summary>
	///     Validates one worked node in isolation — everything decidable without looking at
	///     prerequisite edges.
	/// </summary>
	private static void ValidateWorkShape(
		ImportSubtreeNodeSpec node,
		ImportSubtreeLeafWorkSpec work,
		IReadOnlyDictionary<long, IReadOnlyList<ImportSubtreeNodeSpec>> childrenByParent)
	{
		if (childrenByParent.ContainsKey(node.LocalId)) {
			throw new InvariantViolationException(
				"import-subtree-work-on-branch",
				$"Node {node.LocalId} has children in this batch, so it cannot hold LeafWork.");
		}

		if (work.Achievement is not (Achievement.InProgress or Achievement.Success or Achievement.Cancelled
			or Achievement.Unsuccessful)) {
			throw new InvariantViolationException(
				"import-subtree-invalid-work-achievement",
				$"Node {node.LocalId} records work, so its achievement cannot be {work.Achievement}.");
		}

		if (work.FinishedAt is Instant finishedAt && finishedAt <= work.StartedAt) {
			throw new InvariantViolationException(
				"import-subtree-invalid-work-interval",
				$"Node {node.LocalId}'s work finishes at {finishedAt}, which is not after its start at {work.StartedAt}.");
		}

		if (AchievementTransitions.IsCompletedState(work.Achievement) && work.FinishedAt is null) {
			throw new InvariantViolationException(
				"import-subtree-unfinished-completed-work",
				$"Node {node.LocalId} reaches {work.Achievement} but its session never finishes.");
		}
	}

	/// <summary>
	///     Derives, for every node in the batch, whether it succeeds (spec §5.2: a leaf iff its own work
	///     succeeded, a branch iff every child does) and the latest instant any work beneath it
	///     finished. Walks the batch in reverse parents-before-children order, so each node is visited
	///     only after all of its children — an explicit bottom-up pass rather than recursion, matching
	///     <see cref="Domain.Hierarchy.AchievementCalculator" />'s reason for avoiding the call stack.
	/// </summary>
	private static (Dictionary<long, bool> Achieved, Dictionary<long, Instant?> LatestFinish) DeriveSubtreeOutcomes(
		IReadOnlyList<ImportSubtreeNodeSpec> ordered,
		IReadOnlyDictionary<long, IReadOnlyList<ImportSubtreeNodeSpec>> childrenByParent)
	{
		var achieved = new Dictionary<long, bool>(ordered.Count);
		var latestFinish = new Dictionary<long, Instant?>(ordered.Count);

		for (var i = ordered.Count - 1; i >= 0; i--) {
			var node = ordered[i];

			if (!childrenByParent.TryGetValue(node.LocalId, out var children)) {
				achieved[node.LocalId] = node.LeafWork?.Achievement == Achievement.Success;
				latestFinish[node.LocalId] = node.LeafWork?.FinishedAt;
				continue;
			}

			achieved[node.LocalId] = children.All(child => achieved[child.LocalId]);
			// Max over a nullable sequence ignores nulls and yields null for an all-null subtree.
			latestFinish[node.LocalId] = children.Select(child => latestFinish[child.LocalId]).Max();
		}

		return (achieved, latestFinish);
	}

	/// <summary>
	///     The local ids of every prerequisite gating <paramref name="node" /> — those declared on it and
	///     those declared on any of its batch ancestors, since an ancestor's prerequisite gates its whole
	///     subtree (spec §6, <see cref="Domain.Hierarchy.ReadinessCalculator" />).
	/// </summary>
	private static IEnumerable<long> EffectivePrerequisiteLocalIds(
		ImportSubtreeNodeSpec node, Dictionary<long, ImportSubtreeNodeSpec> byLocalId)
	{
		var current = node;
		while (true) {
			foreach (var requiredLocalId in current.PrerequisiteLocalIds) {
				yield return requiredLocalId;
			}

			if (current.ParentLocalId is not { } parentLocalId) {
				yield break;
			}

			current = byLocalId[parentLocalId];
		}
	}
}
