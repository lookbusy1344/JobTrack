namespace JobTrack.UatSeed;

using Abstractions;

/// <summary>
///     The identifiers <see cref="UatSeeder.SeedAsync" /> created, for a caller (CLI output, a
///     smoke test) that wants to point a tester or an assertion at a specific seeded row.
/// </summary>
public sealed record UatSeedSummary
{
	/// <summary>The seeded <see cref="EmployeeRole.JobManager" />.</summary>
	public required AppUserId JobManagerId { get; init; }

	/// <summary>The seeded <see cref="EmployeeRole.Worker" />.</summary>
	public required AppUserId WorkerId { get; init; }

	/// <summary>The seeded <see cref="EmployeeRole.Requester" />.</summary>
	public required AppUserId RequesterId { get; init; }

	/// <summary>The seeded "IT Helpdesk" holding area.</summary>
	public required RequestHoldingAreaId HoldingAreaId { get; init; }

	/// <summary>The still-unassigned, unacknowledged "Printer will not turn on" request.</summary>
	public required JobNodeId UnassignedRequestNodeId { get; init; }

	/// <summary>The acknowledged, worker-assigned "New starter laptop setup" request.</summary>
	public required JobNodeId AssignedRequestNodeId { get; init; }

	/// <summary>An ordinary unassigned leaf (outside the requester flow) for a pickup-pool demo.</summary>
	public required JobNodeId PoolLeafNodeId { get; init; }

	/// <summary>The leaf blocked on an unsatisfied prerequisite.</summary>
	public required JobNodeId BlockedLeafNodeId { get; init; }

	/// <summary>The leaf with a currently open (unfinished) work session.</summary>
	public required JobNodeId ActiveSessionLeafNodeId { get; init; }

	/// <summary>The leaf with a finished, rated, cost-reportable session.</summary>
	public required JobNodeId CostReportableLeafNodeId { get; init; }
}
