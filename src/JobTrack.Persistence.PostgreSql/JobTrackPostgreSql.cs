namespace JobTrack.Persistence.PostgreSql;

using Application;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using Npgsql;

/// <summary>Composes the PostgreSQL provider behind JobTrack's single public facade.</summary>
public static class JobTrackPostgreSql
{
	/// <summary>Creates a provider-neutral client over one shared pooled data source.</summary>
	public static IJobTrackClient Create(
		NpgsqlDataSource dataSource,
		IPasswordHasher<BootstrapCredentialSubject>? passwordHasher = null,
		IPasswordHasher<EmployeeCredentialSubject>? employeePasswordHasher = null,
		IClock? clock = null)
	{
		ArgumentNullException.ThrowIfNull(dataSource);

		clock ??= SystemClock.Instance;

		var bootstrap = new PostgreSqlInstallationBootstrapPort(dataSource, clock);
		var employees = new PostgreSqlEmployeeQueryPort(dataSource, clock);
		var employeeCommands = new PostgreSqlEmployeeCommandPort(dataSource, clock);
		var readiness = new PostgreSqlReadinessQueryPort(dataSource);
		var browse = new PostgreSqlJobBrowseQueryPort(dataSource);
		var awaitingProgress = new PostgreSqlAwaitingProgressQueryPort(dataSource);
		var jobs = new PostgreSqlJobNodeCommandPort(dataSource, clock);
		var sessions = new PostgreSqlWorkSessionCommandPort(dataSource, clock);
		var leafSessions = new PostgreSqlWorkSessionQueryPort(dataSource, clock);
		var leafWork = new PostgreSqlLeafWorkQueryPort(dataSource);
		var prerequisites = new PostgreSqlPrerequisiteQueryPort(dataSource);
		var scheduleQueries = new PostgreSqlScheduleQueryPort(dataSource, clock);
		var achievements = new PostgreSqlAchievementCommandPort(dataSource, clock);
		var schedules = new PostgreSqlScheduleCommandPort(dataSource, clock);
		var rates = new PostgreSqlRateCommandPort(dataSource, clock);
		var rateQueries = new PostgreSqlRateQueryPort(dataSource, clock);
		var costs = new PostgreSqlCostQueryPort(dataSource, clock);
		var audit = new PostgreSqlAuditQueryPort(dataSource, clock);
		var tokens = new PostgreSqlPersonalAccessTokenPort(dataSource, clock);
		var requests = new PostgreSqlJobRequestCommandPort(dataSource, clock);
		var authenticationAudit = new PostgreSqlAuthenticationAuditPort(dataSource, clock);
		var credentials = new PostgreSqlAccountCredentialPort(
			dataSource, clock, employeePasswordHasher ?? new PasswordHasher<EmployeeCredentialSubject>());
		var costQueries = new CostQueries(costs);

		return new JobTrackClient(
			new InstallationCommands(bootstrap, passwordHasher ?? new PasswordHasher<BootstrapCredentialSubject>()),
			new JobQueries(
				employees, readiness, browse, awaitingProgress, leafSessions, leafWork, prerequisites, scheduleQueries, rateQueries,
				costQueries, clock),
			new EmployeeCommands(employeeCommands, employeePasswordHasher ?? new PasswordHasher<EmployeeCredentialSubject>()),
			new JobCommands(jobs),
			new WorkCommands(sessions, achievements),
			new ScheduleCommands(schedules),
			new RateCommands(rates),
			costQueries,
			new AuditQueries(audit),
			new TokenCommands(tokens, clock),
			new RequestCommands(requests),
			new AuthenticationAuditCommands(authenticationAudit),
			new AccountCredentialCommands(credentials));
	}
}
