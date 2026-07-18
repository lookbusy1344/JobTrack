namespace JobTrack.Application;

/// <summary>
///     The single configured entry point consumers require (plan ?7.1, spec ?13.2). Grouped into
///     cohesive sub-services rather than exposing repositories or a public unit of work; each group is
///     added as its application slice (plan ?7.3) is implemented ? see
///     docs/api/jobtrack-client-design.md for the full intended shape and why the facade grows
///     additively rather than being declared complete up front.
/// </summary>
public interface IJobTrackClient
{
	/// <summary>Installation lifecycle: the one-time atomic bootstrap (plan ?7.3 step 1, ADR 0005).</summary>
	IInstallationCommands Installation { get; }

	/// <summary>Read-only queries: employee profile and account state (plan ?7.3 step 2).</summary>
	IJobQueries Query { get; }

	/// <summary>Employee role-assignment commands: grant and revoke the six baseline roles (plan ?8.3).</summary>
	IEmployeeCommands Employees { get; }

	/// <summary>Job-node structural commands: create, edit, move, archive, delete (plan ?7.3 step 3).</summary>
	IJobCommands Jobs { get; }

	/// <summary>Work-session commands: start, finish, resume, correct (plan ?7.3 step 6).</summary>
	IWorkCommands Work { get; }

	/// <summary>Schedule commands: add schedule versions and exceptions (plan ?7.3 step 8).</summary>
	IScheduleCommands Schedules { get; }

	/// <summary>Rate commands: add user cost rates and node rate overrides (plan ?7.3 step 9).</summary>
	IRateCommands Rates { get; }

	/// <summary>Cost queries: cost details and hierarchy totals (plan ?7.3 step 10).</summary>
	ICostQueries Costs { get; }

	/// <summary>Audit history search (plan ?7.3 step 11).</summary>
	IAuditQueries Audit { get; }

	/// <summary>Personal access token lifecycle commands for the external HTTP API (ADR 0029, ADR 0030).</summary>
	ITokenCommands Tokens { get; }

	/// <summary>Requester intake commands: submit a request into a configured holding area (ADR 0033).</summary>
	IRequestCommands Requests { get; }

	/// <summary>Authentication and credential self-service audit commands.</summary>
	IAuthenticationAuditCommands AuthenticationAudit { get; }

	/// <summary>Credential-sensitive account state transitions.</summary>
	IAccountCredentialCommands Credentials { get; }
}
