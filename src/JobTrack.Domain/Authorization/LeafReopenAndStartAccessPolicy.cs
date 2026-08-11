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
	///     any eligible <see cref="LeafReopenAndStartFacts.TargetWorkedByUserId" />. A
	///     <see cref="EmployeeRole.Worker" /> who recorded a previous session on this leaf
	///     (<see cref="LeafReopenAndStartFacts.ActorParticipatedPreviously" />) but controls nothing may
	///     use the composite only to start a session for themselves -- historical participation grants
	///     the right to get the leaf moving again, never the right to start work for someone else.
	/// </summary>
	public static bool CanReopenAndStartFor(IReadOnlyCollection<EmployeeRole> actorRoles, LeafReopenAndStartFacts facts)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);
		ArgumentNullException.ThrowIfNull(facts);

		var hasElevatedOrControlAuthority = actorRoles.Contains(EmployeeRole.Administrator)
											|| actorRoles.Contains(EmployeeRole.JobManager)
											|| (actorRoles.Contains(EmployeeRole.Worker) && facts.ActorControlsNode);

		if (hasElevatedOrControlAuthority) {
			return true;
		}

		return actorRoles.Contains(EmployeeRole.Worker)
			   && facts.ActorParticipatedPreviously
			   && facts.ActorUserId == facts.TargetWorkedByUserId;
	}
}
