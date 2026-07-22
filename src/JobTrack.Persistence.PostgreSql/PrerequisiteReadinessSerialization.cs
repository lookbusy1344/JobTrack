namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Microsoft.EntityFrameworkCore;

/// <summary>Serializes prerequisite readiness decisions with mutations that invalidate them.</summary>
internal static class PrerequisiteReadinessSerialization
{
	/// <summary>Acquires ADR 0044's transaction-scoped leaf-closure lock for one required job.</summary>
	public static async Task AcquireAsync(
		PostgreSqlJobTrackDbContext context, JobNodeId requiredJobId, CancellationToken cancellationToken)
	{
		_ = await context.Database.ExecuteSqlInterpolatedAsync(
			$"SELECT pg_advisory_xact_lock(jobtrack_leaf_session_closure_lock_key({requiredJobId.Value}));",
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Whether any direct dependent, or a leaf below it, currently has active work.</summary>
	public static async Task<bool> HasActiveDependentWorkAsync(
		PostgreSqlJobTrackDbContext context, JobNodeId requiredJobId, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<bool>(
				$"SELECT jobtrack_has_active_dependent_work({requiredJobId.Value}) AS \"Value\"")
			.SingleAsync(cancellationToken).ConfigureAwait(false);
}
