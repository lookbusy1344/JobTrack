namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for audit history search (plan §7.3 step 11; spec §7.3 role table:
///     "Auditor/read-only | Read job, work, schedule, and audit information without mutation; rate and
///     cost access remains separately controlled"). Unlike ordinary job/schedule visibility — an
///     unqualified baseline for every role (spec §7.3) — the audit event log itself is not: an actor
///     must hold <see cref="EmployeeRole.Auditor" /> or <see cref="EmployeeRole.Administrator" /> to
///     search it at all. Whether a caller may see a sensitive event's before/after payload is a
///     separate, per-event concern (see <see cref="CostAccessPolicy" />), not part of this gate.
/// </summary>
public static class AuditAccessPolicy
{
	/// <summary>
	///     An actor may search audit history if they hold <see cref="EmployeeRole.Administrator" /> or
	///     <see cref="EmployeeRole.Auditor" />.
	/// </summary>
	public static bool CanSearch(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator) || actorRoles.Contains(EmployeeRole.Auditor);
	}
}
