namespace JobTrack.Persistence.Shared.Ports;

using Abstractions;
using Application;
using Application.Ports;
using Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     The provider-neutral body of <see cref="ILeafWorkQueryPort" /> (plan §8.5 slice 5). One
///     <see cref="DbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class LeafWorkQueryPort(IProviderReadOperations provider) : ILeafWorkQueryPort
{
	/// <inheritdoc />
	public async Task<LeafWorkResult> GetLeafWorkAsync(JobNodeId jobNodeId, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

		var leafWork = await context.Set<LeafWorkEntity>().AsNoTracking()
									.FirstOrDefaultAsync(lw => lw.JobNodeId == jobNodeId, cancellationToken).ConfigureAwait(false)
					   ?? throw new EntityNotFoundException($"Job node {jobNodeId} has no LeafWork attached.");

		return new() {
			JobNodeId = leafWork.JobNodeId,
			Achievement = leafWork.Achievement,
			PartialCriteria = leafWork.PartialCriteria,
			FullCriteria = leafWork.FullCriteria,
			ChangedAt = leafWork.ChangedAt,
			Version = leafWork.RowVersion,
		};
	}
}
