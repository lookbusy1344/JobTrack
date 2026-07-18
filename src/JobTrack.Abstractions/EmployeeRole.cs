namespace JobTrack.Abstractions;

/// <summary>
///     The six baseline authorization roles (spec_claude §6.3) plus <see cref="Requester" />
///     (ADR 0033). Values match the seeded <c>identity_role</c> reference-table ids exactly (database
///     schema version 0002), in the order listed there.
/// </summary>
public enum EmployeeRole
{
	/// <summary>No role assignment has been made.</summary>
	None = 0,

	/// <summary>Manages employee accounts, account state, global role assignments, system configuration, and all job data.</summary>
	Administrator = 1,

	/// <summary>Creates, edits, moves, archives, and decomposes planning nodes and leaf work.</summary>
	JobManager = 2,

	/// <summary>Starts, finishes, and corrects their own work sessions.</summary>
	Worker = 3,

	/// <summary>Manages user cost rates and node rate overrides.</summary>
	RateManager = 4,

	/// <summary>Views cost details and hierarchy totals without rate-management rights.</summary>
	CostViewer = 5,

	/// <summary>Searches audit history without job-management or rate-management rights.</summary>
	Auditor = 6,

	/// <summary>
	///     Submits requests into a configured holding area and views a requester-safe progress
	///     projection of their own requests (ADR 0033). Excluded from pickup, structural job
	///     management, work-session recording, and all rate/cost/audit visibility.
	/// </summary>
	Requester = 7,
}
