namespace JobTrack.Application;

internal sealed class JobTrackClient : IJobTrackClient
{
	public JobTrackClient(
		IInstallationCommands installation,
		IJobQueries query,
		IEmployeeCommands employees,
		IJobCommands jobs,
		IWorkCommands work,
		IScheduleCommands schedules,
		IRateCommands rates,
		ICostQueries costs,
		IAuditQueries audit,
		ITokenCommands tokens,
		IRequestCommands requests,
		IAuthenticationAuditCommands authenticationAudit,
		IAccountCredentialCommands credentials)
	{
		Installation = installation;
		Query = query;
		Employees = employees;
		Jobs = jobs;
		Work = work;
		Schedules = schedules;
		Rates = rates;
		Costs = costs;
		Audit = audit;
		Tokens = tokens;
		Requests = requests;
		AuthenticationAudit = authenticationAudit;
		Credentials = credentials;
	}

	public IInstallationCommands Installation { get; }

	public IJobQueries Query { get; }

	public IEmployeeCommands Employees { get; }

	public IJobCommands Jobs { get; }

	public IWorkCommands Work { get; }

	public IScheduleCommands Schedules { get; }

	public IRateCommands Rates { get; }

	public ICostQueries Costs { get; }

	public IAuditQueries Audit { get; }

	public ITokenCommands Tokens { get; }

	public IRequestCommands Requests { get; }

	public IAuthenticationAuditCommands AuthenticationAudit { get; }

	public IAccountCredentialCommands Credentials { get; }
}
