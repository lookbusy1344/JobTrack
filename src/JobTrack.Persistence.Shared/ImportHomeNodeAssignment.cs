namespace JobTrack.Persistence.Shared;

using System.Globalization;
using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Remediation plan §3.3: an import that establishes a home node writes those assignments inside
///     the import's own transaction, not as a post-commit loop of per-account
///     <c>SetHomeNodeAsync</c> calls. That loop could leave a tree imported with only some accounts
///     changed if a later call failed or the process stopped, made one operator intent appear as
///     several unrelated correlations, and required the caller to act as each target account in turn.
///     Here the operator stays the single actor, every target account is an affected entity, and the
///     whole thing commits once or not at all. Defined once for both providers, and called from inside
///     each one's already-open <see cref="DbContext" />/transaction.
/// </summary>
internal static class ImportHomeNodeAssignment
{
	private const string Operation = "set-home-node";

	/// <summary>
	///     Validates <paramref name="homeNodeId" /> and every account in <paramref name="userIds" />,
	///     then points each account's <c>home_node_id</c> at it and queues one
	///     <see cref="AuditEventWriter" /> entry per account under the import's own actor and
	///     correlation identifier. Validation happens before any assignment so a rejected account never
	///     leaves an earlier one written -- though the enclosing transaction would roll that back in any
	///     case, this keeps the failure attributable to the account that caused it.
	/// </summary>
	/// <exception cref="InvariantViolationException">
	///     <paramref name="homeNodeId" /> imported as a leaf (<c>ConstraintId</c>
	///     <c>"home-node-must-not-be-leaf"</c>), which <c>SetHomeNodeAsync</c> also refuses.
	/// </exception>
	/// <exception cref="EntityNotFoundException">One of <paramref name="userIds" /> does not exist.</exception>
	public static async Task ApplyAsync(
		DbContext context, JobNodeId homeNodeId, IReadOnlyList<AppUserId> userIds, AppUserId actorId, Instant now,
		Guid correlationId, CancellationToken cancellationToken)
	{
		// An imported node always has a parent, so "childless" is exactly "leaf" here -- the Root case
		// JobNodeStructuralResults.DeriveKind also handles cannot arise inside an imported subtree.
		if (!await context.Set<JobNodeEntity>().AsNoTracking()
						  .AnyAsync(child => child.ParentId == homeNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"home-node-must-not-be-leaf", $"Job node {homeNodeId} is a leaf and cannot be set as a home node.");
		}

		var users = new List<AppUserEntity>(userIds.Count);
		foreach (var userId in userIds.OrderBy(id => id.Value)) {
			var identityUser = await IdentityUserWriteLock.AcquireAsync(context, userId, cancellationToken).ConfigureAwait(false);
			var isLockedOut = identityUser.LockoutEnabled
							  && identityUser.LockoutEnd is Instant lockoutEnd
							  && lockoutEnd > now;
			if (!identityUser.IsEnabled || isLockedOut) {
				throw new InvariantViolationException(
					"home-node-target-not-active", $"Employee {userId} is disabled or locked and cannot receive a home node.");
			}

			var user = await context.Set<AppUserEntity>()
									.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false)
					   ?? throw new EntityNotFoundException($"Employee {userId} does not exist.");
			users.Add(user);
		}

		foreach (var user in users) {
			var userId = user.Id;
			var previousHomeNodeId = user.HomeNodeId;
			user.HomeNodeId = homeNodeId;

			AuditEventWriter.Add(
				context, actorId, now, Operation, "app_user", userId.Value, correlationId, null,
				new Dictionary<string, string?> {
					["home_node_id"] = previousHomeNodeId?.Value.ToString(CultureInfo.InvariantCulture),
				},
				new Dictionary<string, string?> {
					["home_node_id"] = homeNodeId.Value.ToString(CultureInfo.InvariantCulture),
				});
		}
	}
}
