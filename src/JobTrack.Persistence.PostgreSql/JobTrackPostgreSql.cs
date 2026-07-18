namespace JobTrack.Persistence.PostgreSql;

using Application;
using Microsoft.AspNetCore.Identity;
using Npgsql;

/// <summary>Composes the PostgreSQL provider behind JobTrack's single public facade.</summary>
public static class JobTrackPostgreSql
{
	/// <summary>Creates a provider-neutral client over one shared pooled data source.</summary>
	public static IJobTrackClient Create(
		NpgsqlDataSource dataSource,
		IPasswordHasher<BootstrapCredentialSubject>? passwordHasher = null,
		IPasswordHasher<EmployeeCredentialSubject>? employeePasswordHasher = null)
	{
		ArgumentNullException.ThrowIfNull(dataSource);

		var bootstrap = new PostgreSqlInstallationBootstrapPort(dataSource);
		var employees = new PostgreSqlEmployeeQueryPort(dataSource);
		var employeeCommands = new PostgreSqlEmployeeCommandPort(dataSource);
		var readiness = new PostgreSqlReadinessQueryPort(dataSource);
		var browse = new PostgreSqlJobBrowseQueryPort(dataSource);
		var awaitingProgress = new PostgreSqlAwaitingProgressQueryPort(dataSource);
		var jobs = new PostgreSqlJobNodeCommandPort(dataSource);
		var sessions = new PostgreSqlWorkSessionCommandPort(dataSource);
		var leafSessions = new PostgreSqlWorkSessionQueryPort(dataSource);
		var leafWork = new PostgreSqlLeafWorkQueryPort(dataSource);
		var prerequisites = new PostgreSqlPrerequisiteQueryPort(dataSource);
		var scheduleQueries = new PostgreSqlScheduleQueryPort(dataSource);
		var achievements = new PostgreSqlAchievementCommandPort(dataSource);
		var schedules = new PostgreSqlScheduleCommandPort(dataSource);
		var rates = new PostgreSqlRateCommandPort(dataSource);
		var rateQueries = new PostgreSqlRateQueryPort(dataSource);
		var costs = new PostgreSqlCostQueryPort(dataSource);
		var audit = new PostgreSqlAuditQueryPort(dataSource);
		var tokens = new PostgreSqlPersonalAccessTokenPort(dataSource);
		var requests = new PostgreSqlJobRequestCommandPort(dataSource);
		var authenticationAudit = new PostgreSqlAuthenticationAuditPort(dataSource);
		var credentials = new PostgreSqlAccountCredentialPort(dataSource);
		var costQueries = new CostQueries(costs);

		return new JobTrackClient(
			new InstallationCommands(bootstrap, passwordHasher ?? new PasswordHasher<BootstrapCredentialSubject>()),
			new JobQueries(
				employees, readiness, browse, awaitingProgress, leafSessions, leafWork, prerequisites, scheduleQueries, rateQueries,
				costQueries),
			new EmployeeCommands(employeeCommands, employeePasswordHasher ?? new PasswordHasher<EmployeeCredentialSubject>()),
			new JobCommands(jobs),
			new WorkCommands(sessions, achievements),
			new ScheduleCommands(schedules),
			new RateCommands(rates),
			costQueries,
			new AuditQueries(audit),
			new TokenCommands(tokens),
			new RequestCommands(requests),
			new AuthenticationAuditCommands(authenticationAudit),
			new AccountCredentialCommands(credentials));
	}
}
