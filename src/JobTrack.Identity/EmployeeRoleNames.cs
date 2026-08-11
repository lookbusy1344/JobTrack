namespace JobTrack.Identity;

/// <summary>
///     The seven seeded <c>identity_role.name</c> canonical strings (schema version 0002; spec_claude
///     §6.3; <see cref="Requester" /> added by ADR 0033), shared between <see cref="JobTrackUserStore" />'s
///     role-claim lookups and <c>JobTrack.Web</c>'s named authorization policies so neither restates
///     these literals independently (CLAUDE.md: no magic strings).
/// </summary>
public static class EmployeeRoleNames
{
	public const string Administrator = "Administrator";

	public const string JobManager = "Job manager";

	public const string Worker = "Worker";

	public const string RateManager = "Rate manager";

	public const string CostViewer = "Cost viewer";

	public const string Auditor = "Auditor";

	public const string Requester = "Requester";
}
