namespace JobTrack.Persistence.Shared;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     The recursive ancestor walk backing <c>job_node</c> command-port authorization (impl plan §7.3
///     slice 3, §7.4's sanctioned "recursive hierarchy queries" exception to EF-first). Portable
///     ANSI <c>WITH RECURSIVE</c> SQL, identical for both providers, so it lives here rather than
///     being duplicated per provider.
/// </summary>
public static class JobNodeHierarchyQueries
{
	/// <summary>
	///     Returns the <c>owner_user_id</c> of <paramref name="nodeId" /> and every one of its ancestors
	///     up to the root, skipping ancestors with a <see langword="null" /> owner (ownership model §3:
	///     an unassigned node on the path contributes no controller). Note this means an empty result is
	///     <em>not</em> a reliable "does <paramref name="nodeId" /> exist" signal once owners can be null;
	///     callers needing existence must check separately. Callers derive "does the actor own this node
	///     or an ancestor" from this single query.
	/// </summary>
	public static async Task<IReadOnlyList<long>> GetAncestorOwnerIdsAsync(
		DbContext context, long nodeId, CancellationToken cancellationToken)
	{
		var ownerIds = await context.Database.SqlQuery<long>(
			$"""
			 WITH RECURSIVE ancestors(id, owner_user_id, parent_id) AS (
			     SELECT id, owner_user_id, parent_id FROM job_node WHERE id = {nodeId}
			     UNION ALL
			     SELECT jn.id, jn.owner_user_id, jn.parent_id
			     FROM job_node jn JOIN ancestors a ON jn.id = a.parent_id
			 )
			 SELECT owner_user_id FROM ancestors WHERE owner_user_id IS NOT NULL
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

		return ownerIds;
	}

	/// <summary>
	///     Returns <paramref name="nodeId" /> itself and every one of its ancestors up to the root, in
	///     one round trip -- used to check whether one node is an ancestor/descendant of another (spec
	///     §6 rule 5: a prerequisite edge cannot connect nodes in an ancestor/descendant relationship).
	/// </summary>
	public static async Task<IReadOnlyList<long>> GetAncestorIdsAsync(
		DbContext context, long nodeId, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<long>(
			$"""
			 WITH RECURSIVE ancestors(id, parent_id) AS (
			     SELECT id, parent_id FROM job_node WHERE id = {nodeId}
			     UNION ALL
			     SELECT jn.id, jn.parent_id
			     FROM job_node jn JOIN ancestors a ON jn.id = a.parent_id
			 )
			 SELECT id FROM ancestors
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>
	///     Whether adding a <c>job_prerequisite</c> edge <paramref name="requiredJobId" /> -&gt;
	///     <paramref name="dependentJobId" /> would close a cycle in the existing prerequisite graph:
	///     walks forward from <paramref name="dependentJobId" /> along existing edges and checks whether
	///     <paramref name="requiredJobId" /> is already reachable (spec §6 rule 4), mirroring
	///     <c>check_job_prerequisite_no_cycle</c> (schema version 0008).
	/// </summary>
	public static async Task<bool> PrerequisiteWouldCreateCycleAsync(
		DbContext context, long requiredJobId, long dependentJobId, CancellationToken cancellationToken)
	{
		var reachable = await context.Database.SqlQuery<bool>(
			$"""
			 WITH RECURSIVE reachable(id) AS (
			     SELECT to_id FROM job_prerequisite WHERE from_id = {dependentJobId}
			     UNION
			     SELECT jp.to_id FROM job_prerequisite jp JOIN reachable r ON jp.from_id = r.id
			 )
			 SELECT EXISTS (SELECT 1 FROM reachable WHERE id = {requiredJobId}) AS "Value"
			 """).SingleAsync(cancellationToken).ConfigureAwait(false);

		return reachable;
	}

	/// <summary>
	///     Returns <paramref name="nodeId" /> and every one of its ancestors up to the root, each paired
	///     with its own <c>parent_id</c>, in one round trip -- the ancestor chain
	///     <c>Domain.Hierarchy.ReadinessCalculator</c>'s upward walk needs (impl plan §7.3 slice 6:
	///     "the start... command shall recheck prerequisites"). An empty result means
	///     <paramref name="nodeId" /> does not exist.
	/// </summary>
	public static async Task<IReadOnlyList<AncestorChainRow>> GetAncestorChainAsync(
		DbContext context, long nodeId, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<AncestorChainRow>(
			$"""
			 WITH RECURSIVE ancestors(id, parent_id) AS (
			     SELECT id, parent_id FROM job_node WHERE id = {nodeId}
			     UNION ALL
			     SELECT jn.id, jn.parent_id
			     FROM job_node jn JOIN ancestors a ON jn.id = a.parent_id
			 )
			 SELECT id AS Id, parent_id AS ParentId FROM ancestors
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>
	///     Returns <paramref name="rootId" /> and its complete descendant subtree, each row carrying its
	///     own <c>parent_id</c> and, if it owns a <c>leaf_work</c> row, that row's <c>achievement_id</c>
	///     -- exactly the facts <c>Domain.Hierarchy.AchievementCalculator</c> needs to derive recursive
	///     achievement for a required job (impl plan §7.3 slice 6). A branch's own achievement depends
	///     on its whole subtree, not just the node itself, so this is not scoped further.
	/// </summary>
	public static async Task<IReadOnlyList<SubtreeAchievementRow>> GetSubtreeAchievementsAsync(
		DbContext context, long rootId, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<SubtreeAchievementRow>(
			$"""
			 WITH RECURSIVE subtree(id, parent_id) AS (
			     SELECT id, parent_id FROM job_node WHERE id = {rootId}
			     UNION ALL
			     SELECT jn.id, jn.parent_id
			     FROM job_node jn JOIN subtree s ON jn.parent_id = s.id
			 )
			 SELECT s.id AS Id, s.parent_id AS ParentId, lw.achievement_id AS AchievementId
			 FROM subtree s LEFT JOIN leaf_work lw ON lw.job_node_id = s.id
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>
	///     Returns <paramref name="rootId" /> (a request's anchor node) and its complete descendant
	///     subtree with the requester-safe fields only -- description, achievement (childless nodes
	///     only; <see langword="null" /> for a branch or a childless node with no <c>LeafWork</c> yet),
	///     and whether the node is childless (ADR 0034, plan §7). No owner, rates, sessions, schedules,
	///     or audit fields are selected. Deliberately excludes timestamps: an ad-hoc <c>SqlQuery&lt;T&gt;</c>
	///     row type has no access to either provider's entity-scoped <c>Instant</c> value-converter
	///     configuration (SQLite's is registered per-entity-property in <c>OnModelCreating</c>, not
	///     provider-wide the way PostgreSQL's <c>UseNodaTime()</c> is), so callers needing
	///     last-updated instants look them up separately through the typed <c>JobNodeEntity</c>/
	///     <c>LeafWorkEntity</c> sets, which do have that configuration. Unbounded, like every other
	///     recursive hierarchy query here -- termination relies on the DB-enforced cycle-free invariant
	///     (schema version 0005), not a depth/row cap.
	/// </summary>
	public static async Task<IReadOnlyList<RequesterSubtreeRow>> GetRequesterSubtreeAsync(
		DbContext context, long rootId, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<RequesterSubtreeRow>(
			$"""
			 WITH RECURSIVE subtree(id, parent_id) AS (
			     SELECT id, parent_id FROM job_node WHERE id = {rootId}
			     UNION ALL
			     SELECT jn.id, jn.parent_id
			     FROM job_node jn JOIN subtree st ON jn.parent_id = st.id
			 )
			 SELECT
			     s.id AS Id,
			     s.parent_id AS ParentId,
			     jn.description AS Description,
			     lw.achievement_id AS AchievementId,
			     NOT EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = s.id) AS IsChildless
			 FROM subtree s
			 JOIN job_node jn ON jn.id = s.id
			 LEFT JOIN leaf_work lw ON lw.job_node_id = s.id
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>
	///     Returns <paramref name="rootId" /> and a depth/breadth-bounded slice of its descendant subtree
	///     (Browse multi-level tree, ADR 0039) -- one round trip, no persisted nested-set columns (the
	///     schema is adjacency-list only; see ADR 0039's correction to the plan's original premise).
	///     Recursion never passes <paramref name="maxDepth" /> levels below the root. The root's own
	///     immediate children are always fully included (never breadth-capped); for every deeper node,
	///     only the first <paramref name="breadthCap" /> children by <c>id</c> order (<c>rn</c>, each
	///     child's 1-based rank among its own siblings, computed via a correlated count rather than
	///     <c>ROW_NUMBER()</c> -- SQLite rejects window functions inside a recursive CTE term) have their
	///     own children fetched -- <see cref="BoundedSubtreeRow.WasExpanded" /> tells the caller whether a
	///     row's own children were actually fetched, so it can flag <c>HasUnexpandedChildren</c> together
	///     with a separate existence check.
	/// </summary>
	public static async Task<IReadOnlyList<BoundedSubtreeRow>> GetBoundedSubtreeAsync(
		DbContext context, long rootId, int maxDepth, int breadthCap, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<BoundedSubtreeRow>(
			$"""
			 WITH RECURSIVE subtree(id, parent_id, depth, rn) AS (
			     SELECT id, parent_id, 0, 1 FROM job_node WHERE id = {rootId}
			     UNION ALL
			     SELECT c.id, c.parent_id, p.depth + 1,
			         CAST((SELECT COUNT(*) FROM job_node sib WHERE sib.parent_id = c.parent_id AND sib.id <= c.id) AS INTEGER)
			     FROM job_node c JOIN subtree p ON c.parent_id = p.id
			     WHERE p.depth < {maxDepth}
			       AND (p.depth = 0 OR p.rn <= {breadthCap})
			 )
			 SELECT
			     s.id AS Id,
			     s.parent_id AS ParentId,
			     s.depth AS Depth,
			     (s.depth < {maxDepth} AND (s.depth = 0 OR s.rn <= {breadthCap})) AS WasExpanded
			 FROM subtree s
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>One row of <see cref="JobNodeHierarchyQueries.GetAncestorChainAsync" />.</summary>
public sealed record AncestorChainRow(long Id, long? ParentId);

/// <summary>One row of <see cref="JobNodeHierarchyQueries.GetSubtreeAchievementsAsync" />.</summary>
public sealed record SubtreeAchievementRow(long Id, long? ParentId, short? AchievementId);

/// <summary>One row of <see cref="JobNodeHierarchyQueries.GetRequesterSubtreeAsync" />.</summary>
public sealed record RequesterSubtreeRow(long Id, long? ParentId, string Description, short? AchievementId, bool IsChildless);

/// <summary>One row of <see cref="JobNodeHierarchyQueries.GetBoundedSubtreeAsync" />.</summary>
public sealed record BoundedSubtreeRow(long Id, long? ParentId, int Depth, bool WasExpanded);
