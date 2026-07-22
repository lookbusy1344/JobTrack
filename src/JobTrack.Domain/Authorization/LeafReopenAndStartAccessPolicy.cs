namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for the atomic reopen-and-start composite (ADR 0045 §2). This governs
///     <c>ReopenAndStartWorkAsync</c> only -- reopening in isolation, with no session following it
///     (<c>ReopenWithoutStartingAsync</c>), keeps ADR 0001's original
///     <see cref="EmployeeRole.Administrator" />/<see cref="EmployeeRole.JobManager" />-only
///     restriction unchanged and is governed by <see cref="AchievementAccessPolicy" /> with
///     <c>isReopening: true</c>, not by this policy.
/// </summary>
public static class LeafReopenAndStartAccessPolicy
{
	/// <summary>
	///     Whether the actor may reopen this terminal leaf and start the named target worker's session,
	///     in one atomic composite. Authorization comes from any of three sources (ADR 0045 §2):
	///     <see cref="EmployeeRole.Administrator" />, <see cref="EmployeeRole.JobManager" />, or a
	///     <see cref="EmployeeRole.Worker" /> who controls the leaf's node may start the composite for
	///     any eligible <paramref name="targetWorkedByUserId" />. A <see cref="EmployeeRole.Worker" /> who
	///     recorded a previous session on this leaf (<paramref name="actorParticipatedPreviously" />) but
	///     controls nothing may use the composite only to start a session for themselves -- historical
	///     participation grants the right to get the leaf moving again, never the right to start work for
	///     someone else.
	/// </summary>
	public static bool CanReopenAndStartFor(
		IReadOnlyCollection<EmployeeRole> actorRoles,
		bool actorControlsNode,
		bool actorParticipatedPreviously,
		AppUserId actorUserId,
		AppUserId targetWorkedByUserId)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		var hasElevatedOrControlAuthority = actorRoles.Contains(EmployeeRole.Administrator)
											|| actorRoles.Contains(EmployeeRole.JobManager)
											|| (actorRoles.Contains(EmployeeRole.Worker) && actorControlsNode);

		if (hasElevatedOrControlAuthority) {
			return true;
		}

		return actorRoles.Contains(EmployeeRole.Worker) && actorParticipatedPreviously && actorUserId == targetWorkedByUserId;
	}
}
