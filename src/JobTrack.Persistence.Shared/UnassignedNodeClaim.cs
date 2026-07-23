namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     The single conditional-claim primitive backing every "bring a node out of the unassigned pool"
///     path (remediation plan §2.4): explicit <c>PickUpAsync</c> and the session-start auto-claim ADR
///     0048 added (<c>StartSessionAsync</c>/<c>StartWorkAsync</c>/<c>ReopenAndStartWorkAsync</c>) all
///     need the identical race-safe compare-and-swap, and previously each provider repeated its own
///     hand-written <c>UPDATE job_node ... WHERE owner_user_id IS NULL</c> to get it. EF's
///     <c>ExecuteUpdateAsync</c> expresses the same conditional update in LINQ, translates
///     identically on both providers, and needs no hand-written SQL at all -- the EF-first rule's
///     preferred outcome over merely centralizing the raw SQL string.
/// </summary>
internal static class UnassignedNodeClaim
{
	/// <summary>
	///     Attempts to claim <paramref name="nodeId" /> for <paramref name="claimantUserId" />: the
	///     conditional <c>WHERE owner_user_id IS NULL</c> is the concurrency mechanism itself (plan §6
	///     risk note) -- a concurrent claimant that commits first leaves zero rows affected here, so
	///     this returns <see langword="false" /> rather than silently overwriting their claim. Callers
	///     run this inside their own already-open transaction; it issues no transaction of its own.
	/// </summary>
	public static async Task<bool> TryClaimAsync(
		DbContext context, JobNodeId nodeId, AppUserId claimantUserId, CancellationToken cancellationToken)
	{
		var affected = await context.Set<JobNodeEntity>()
			.Where(n => n.Id == nodeId && n.OwnerUserId == null)
			.ExecuteUpdateAsync(
				setters => setters
					.SetProperty(n => n.OwnerUserId, claimantUserId)
					.SetProperty(n => n.RowVersion, n => n.RowVersion + 1),
				cancellationToken)
			.ConfigureAwait(false);

		return affected > 0;
	}
}
