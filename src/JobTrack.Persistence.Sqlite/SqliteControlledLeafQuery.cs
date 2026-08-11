namespace JobTrack.Persistence.Sqlite;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     SQLite's provider-local equivalent of PostgreSQL's
///     <c>job_node_controlled_leaf_ids</c> stored function. SQLite cannot expose
///     a composable stored set-returning function, so the recursive query stays
///     here as a minimal parameterized statement rather than leaking into the
///     shared persistence layer.
/// </summary>
internal static class SqliteControlledLeafQuery
{
	/// <summary>Returns requested leaf ids controlled by <paramref name="actorId" /> in one round trip.</summary>
	public static async Task<IReadOnlyList<long>> GetControlledLeafIdsAsync(
		DbContext context, long actorId, IReadOnlyList<long> leafIds, CancellationToken cancellationToken)
	{
		if (leafIds.Count == 0) {
			return [];
		}

		var leafIdParameters = leafIds.Select((_, index) => $"@leafId{index}").ToArray();
		var sql = $"""
				   WITH RECURSIVE ancestors(origin_leaf_id, id, owner_user_id, parent_id) AS (
				       SELECT id, id, owner_user_id, parent_id
				       FROM job_node
				       WHERE id IN ({string.Join(',', leafIdParameters)})
				       UNION ALL
				       SELECT a.origin_leaf_id, jn.id, jn.owner_user_id, jn.parent_id
				       FROM job_node jn
				       JOIN ancestors a ON jn.id = a.parent_id
				   )
				   SELECT DISTINCT origin_leaf_id AS "Value"
				   FROM ancestors
				   WHERE owner_user_id = @actorId
				   """;
		var parameters = new List<object>(leafIds.Count + 1) { new SqliteParameter("@actorId", actorId) };
		parameters.AddRange(leafIds.Select((leafId, index) => new SqliteParameter(leafIdParameters[index], leafId)));

		return await context.Database.SqlQueryRaw<long>(sql, [.. parameters])
			.ToListAsync(cancellationToken).ConfigureAwait(false);
	}
}
