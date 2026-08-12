namespace JobTrack.Persistence.Shared;

using Abstractions;
using Application;
using Domain.Hierarchy;
using Entities;
using Microsoft.EntityFrameworkCore;

internal static class JobRequestPersistence
{
	public static async Task<IReadOnlyList<long>> RequireRequesterJobAsync(
		DbContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		var nodeExists = await context.Set<JobNodeEntity>().AsNoTracking()
			.AnyAsync(node => node.Id == nodeId, cancellationToken).ConfigureAwait(false);
		if (!nodeExists) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		var isRequesterJob = await context.Set<JobRequestEntity>().AsNoTracking()
			.AnyAsync(request => request.JobNodeId == nodeId, cancellationToken).ConfigureAwait(false);
		if (!isRequesterJob) {
			throw new InvariantViolationException("requester-job-required", $"Job node {nodeId} has no associated job_request row.");
		}

		return await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, nodeId.Value, cancellationToken).ConfigureAwait(false);
	}

	public static IReadOnlyCollection<RequesterSubtreeLeafState> ToLeafStates(IEnumerable<RequesterSubtreeRow> rows) => [
		.. rows.Select(row => new RequesterSubtreeLeafState {
			LeafAchievement = row.AchievementId.HasValue ? (Achievement)row.AchievementId.Value : null,
		}),
	];

	public static JobRequestNoteResult ToResult(JobRequestNoteEntity note) => new() {
		Id = note.Id,
		AuthorUserId = note.AuthorUserId,
		Content = note.Content,
		VisibleToRequester = note.IsVisibleToRequester,
		CreatedAt = note.CreatedAt,
	};

	public static JobRequestResult ToResult(JobNodeEntity node, JobRequestEntity request) => new() {
		JobNodeId = node.Id,
		HoldingAreaId = request.HoldingAreaId,
		RequesterUserId = request.RequesterUserId,
		OwnerUserId = node.OwnerUserId,
		Description = node.Description,
		SubmittedAt = request.SubmittedAt,
		AcknowledgedAt = request.AcknowledgedAt,
		Version = request.RowVersion,
	};
}
