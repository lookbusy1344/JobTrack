namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using Shared.Entities;

/// <summary>
///     ADR 0044's <c>sessionStartClosed</c> predicate (terminal achievement or archived node),
///     evaluated application-side inside the caller's open transaction for a clear early error;
///     schema version 0007's deferred constraint triggers remain the authority under races and
///     direct-write bypass. Duplicated across the two providers (not across the callers within
///     one), matching this codebase's established convention (see <c>LeafReadiness</c>).
/// </summary>
internal static class LeafSessionClosure
{
	/// <summary>Whether <paramref name="leafId" />'s <c>LeafWork</c> is currently closed to a new active session.</summary>
	public static async Task<bool> IsClosedAsync(
		PostgreSqlJobTrackDbContext context, JobNodeId leafId, CancellationToken cancellationToken)
	{
		var row = await context.Set<LeafWorkEntity>().AsNoTracking()
			.Where(lw => lw.JobNodeId == leafId)
			.Join(
				context.Set<JobNodeEntity>().AsNoTracking(), lw => lw.JobNodeId, n => n.Id,
				(lw, n) => new { lw.Achievement, n.ArchivedAt })
			.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

		return row is not null && (AchievementTransitions.IsCompletedState(row.Achievement) || row.ArchivedAt is not null);
	}

	/// <summary>Whether any <c>work_session</c> on <paramref name="leafId" /> is currently active.</summary>
	public static Task<bool> HasActiveSessionAsync(
		PostgreSqlJobTrackDbContext context, JobNodeId leafId, CancellationToken cancellationToken) =>
		context.Set<WorkSessionEntity>().AsNoTracking()
			.AnyAsync(s => s.LeafWorkId == leafId && s.FinishedAt == null, cancellationToken);
}
