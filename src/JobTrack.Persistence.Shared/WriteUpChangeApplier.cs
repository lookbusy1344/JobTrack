namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     The narrow write-up-only update backing the composite work-ending commands
///     (remediation plan §2.1: <c>CompleteLeafAsync</c>'s optional write-up change and
///     <c>FinishSessionAndUpdateWriteUpAsync</c>), reused by both providers so the two never drift on
///     what "no change" means or what gets audited. Deliberately narrower than a full-replace
///     <c>EditAsync</c> -- it touches only <see cref="JobNodeEntity.WriteUp" /> and audits only that
///     field, matching the narrow field-level audits other single-purpose mutations in this project
///     already write (e.g. <c>FinishSessionAsync</c>'s own <c>finished_at</c>-only audit).
/// </summary>
internal static class WriteUpChangeApplier
{
	/// <summary>
	///     Loads <paramref name="leafId" />'s node (tracked), checks <paramref name="expectedNodeVersion" />,
	///     and -- only if the text actually differs from what is stored -- updates
	///     <see cref="JobNodeEntity.WriteUp" />, bumps its row version, and queues a narrow
	///     <c>edit-job-node</c> audit event under <paramref name="correlationId" />. Callers add the
	///     returned entity's own <c>SaveChangesAsync</c> alongside their other mutations, in the same
	///     transaction.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException"><paramref name="expectedNodeVersion" /> is stale.</exception>
	public static async Task<(bool Changed, JobNodeEntity Node)> ApplyAsync(
		DbContext context,
		JobNodeId leafId,
		long expectedNodeVersion,
		string? writeUp,
		AppUserId actorId,
		Guid correlationId,
		Instant now,
		CancellationToken cancellationToken)
	{
		var node = await context.Set<JobNodeEntity>().FirstOrDefaultAsync(n => n.Id == leafId, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {leafId} does not exist.");

		if (node.RowVersion != expectedNodeVersion) {
			throw new ConcurrencyConflictException(
				$"Expected version {expectedNodeVersion} for job node {leafId} did not match its current version.");
		}

		if (node.WriteUp == writeUp) {
			return (false, node);
		}

		var before = node.WriteUp;
		node.WriteUp = writeUp;
		node.RowVersion += 1;

		AuditEventWriter.Add(
			context, actorId, now, "edit-job-node", "job_node", node.Id.Value, correlationId, null,
			new Dictionary<string, string?> { ["write_up"] = before },
			new Dictionary<string, string?> { ["write_up"] = node.WriteUp });

		return (true, node);
	}
}
