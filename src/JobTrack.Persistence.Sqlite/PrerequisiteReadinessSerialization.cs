namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Microsoft.EntityFrameworkCore;

/// <summary>Checks the prerequisite state protected by SQLite's transaction-wide writer serialization.</summary>
internal static class PrerequisiteReadinessSerialization
{
	/// <summary>Whether any direct dependent, or a leaf below it, currently has active work.</summary>
	public static async Task<bool> HasActiveDependentWorkAsync(
		SqliteJobTrackDbContext context, JobNodeId requiredJobId, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<bool>(
			$"""
			 WITH RECURSIVE dependent_subtrees(id) AS (
			     SELECT to_id FROM job_prerequisite WHERE from_id = {requiredJobId.Value}
			     UNION
			     SELECT jn.id FROM job_node jn JOIN dependent_subtrees ds ON jn.parent_id = ds.id
			 )
			 SELECT EXISTS (
			     SELECT 1
			     FROM work_session ws
			     JOIN dependent_subtrees ds ON ds.id = ws.leaf_work_id
			     WHERE ws.finished_at IS NULL
			 ) AS "Value"
			 """).SingleAsync(cancellationToken).ConfigureAwait(false);
}
