namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rule for the general job/work query surface — job-node browsing, search,
///     subtree, readiness, awaiting-progress, leaf-work, and prerequisite reads (remediation plan
///     §2.4). Every operational employee role may browse job data unconditionally (spec §7.3);
///     <see cref="EmployeeRole.Requester" /> may not, even combined with an operational role — ADR
///     0033 is explicit that requester intake is "never a relaxation of <c>/Jobs/Browse</c> or the
///     general job/work query surface," which stays reachable only through its own requester-safe
///     projection. This policy governs admission only: it does not narrow an operational employee to
///     an owned subtree, which stays a per-node/per-cost concern of <see cref="JobNodeAccessPolicy" />
///     and <see cref="CostAccessPolicy" />.
/// </summary>
public static class JobDataAccessPolicy
{
	/// <summary>
	///     An actor may browse the general job/work query surface if they hold at least one of the six
	///     baseline operational roles: <see cref="EmployeeRole.Administrator" />,
	///     <see cref="EmployeeRole.JobManager" />, <see cref="EmployeeRole.Worker" />,
	///     <see cref="EmployeeRole.RateManager" />, <see cref="EmployeeRole.CostViewer" />, or
	///     <see cref="EmployeeRole.Auditor" />. An actor holding <see cref="EmployeeRole.Requester" />
	///     — alone or combined with an operational role — or no role at all may not.
	/// </summary>
	public static bool CanBrowseJobData(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return !actorRoles.Contains(EmployeeRole.Requester)
			   && (actorRoles.Contains(EmployeeRole.Administrator)
				   || actorRoles.Contains(EmployeeRole.JobManager)
				   || actorRoles.Contains(EmployeeRole.Worker)
				   || actorRoles.Contains(EmployeeRole.RateManager)
				   || actorRoles.Contains(EmployeeRole.CostViewer)
				   || actorRoles.Contains(EmployeeRole.Auditor));
	}
}
