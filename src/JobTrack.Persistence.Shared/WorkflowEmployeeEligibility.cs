namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Revalidates an account at the write boundary before it is assigned technical ownership or
///     recorded work. Requester is deliberately disqualifying even when combined with a workflow
///     role: requester accounts belong to the intake/progress surface, not the operational workforce.
/// </summary>
internal static class WorkflowEmployeeEligibility
{
	public static async Task EnsureMayGrantRequesterRoleAsync(
		DbContext context, AppUserId targetId, CancellationToken cancellationToken)
	{
		_ = await IdentityUserWriteLock.AcquireAsync(context, targetId, cancellationToken).ConfigureAwait(false);

		var ownsJob = await context.Set<JobNodeEntity>().AsNoTracking()
			.AnyAsync(node => node.OwnerUserId == targetId, cancellationToken).ConfigureAwait(false);
		var hasActiveSession = await context.Set<WorkSessionEntity>().AsNoTracking()
			.AnyAsync(session => session.WorkedByUserId == targetId && session.FinishedAt == null, cancellationToken).ConfigureAwait(false);

		if (ownsJob || hasActiveSession) {
			throw new InvariantViolationException(
				"requester-role-assigned-work",
				$"Employee {targetId} cannot become a requester while they own a job or have an active work session.");
		}
	}

	public static async Task EnsureMayBeAssignedWorkAsync(
		DbContext context, AppUserId? targetId, Instant now, string constraintId, CancellationToken cancellationToken)
	{
		if (!targetId.HasValue) {
			return;
		}

		var identityUser = await IdentityUserWriteLock.AcquireAsync(context, targetId.Value, cancellationToken).ConfigureAwait(false);

		var isLockedOut = identityUser.LockoutEnabled
						  && identityUser.LockoutEnd is Instant lockoutEnd
						  && lockoutEnd > now;
		if (!identityUser.IsEnabled || isLockedOut) {
			throw new InvariantViolationException(
				constraintId, $"Employee {targetId.Value} is disabled or locked and cannot be assigned work.");
		}

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == identityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		var hasWorkflowRole = roles.Any(role => role is EmployeeRole.Administrator
			or EmployeeRole.JobManager
			or EmployeeRole.Worker);
		if (!hasWorkflowRole || roles.Contains(EmployeeRole.Requester)) {
			throw new InvariantViolationException(
				constraintId, $"Employee {targetId.Value} is not eligible to be assigned technical work.");
		}
	}

}
