namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Application;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     The root/children validation and <c>LeafWork</c> creation shared by
///     <see cref="SqliteJobNodeCommandPort.AttachLeafWorkAsync" /> and
///     <see cref="SqliteWorkSessionCommandPort.StartWorkAsync" />'s attach-if-missing step, so the two
///     callers cannot drift on what makes a node eligible to hold <c>LeafWork</c>. The caller is
///     responsible for checking whether <c>LeafWork</c> already exists (the two callers react to that
///     differently: one throws, the other treats it as already satisfied) and for authorization.
/// </summary>
internal static class LeafWorkAttachSupport
{
	/// <summary>
	///     Validates <paramref name="node" /> is a childless non-root node, creates its
	///     <c>LeafWork</c> at <see cref="Achievement.Waiting" />, and records the <c>attach-leaf-work</c>
	///     audit event -- caller must already know no <c>LeafWork</c> exists for this node.
	/// </summary>
	public static async Task<LeafWorkEntity> CreateAsync(
		SqliteJobTrackDbContext context, JobNodeEntity node, Instant now, CommandContext commandContext,
		string? partialCriteria, string? fullCriteria, CancellationToken cancellationToken)
	{
		if (node.ParentId is null) {
			throw new InvariantViolationException(
				"job-node-is-root-cannot-attach-leaf-work", "The root job node cannot hold LeafWork.");
		}

		if (await context.Set<JobNodeEntity>().AsNoTracking()
				.AnyAsync(c => c.ParentId == node.Id, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"job-node-has-children-cannot-attach-leaf-work", "A node with children cannot hold LeafWork.");
		}

		var leafWork = new LeafWorkEntity {
			JobNodeId = node.Id,
			Achievement = Achievement.Waiting,
			PartialCriteria = partialCriteria,
			FullCriteria = fullCriteria,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(leafWork);

		AuditEventWriter.Add(
			context, commandContext.Actor, now, "attach-leaf-work", "leaf_work", leafWork.JobNodeId.Value,
			commandContext.CorrelationId, null, null,
			new Dictionary<string, string?> {
				["achievement"] = leafWork.Achievement.ToString(),
				["partial_criteria"] = leafWork.PartialCriteria,
				["full_criteria"] = leafWork.FullCriteria,
			});

		return leafWork;
	}
}
