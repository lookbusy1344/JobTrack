namespace JobTrack.Persistence.Sqlite;

using Application;
using Microsoft.AspNetCore.Identity;

/// <summary>Composes the SQLite provider behind JobTrack's single public facade.</summary>
public static class JobTrackSqlite
{
	/// <summary>Creates a provider-neutral client over the configured SQLite database.</summary>
	public static IJobTrackClient Create(
		string connectionString,
		IPasswordHasher<BootstrapCredentialSubject>? passwordHasher = null,
		IPasswordHasher<EmployeeCredentialSubject>? employeePasswordHasher = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

		var bootstrap = new SqliteInstallationBootstrapPort(connectionString);
		var employees = new SqliteEmployeeQueryPort(connectionString);
		var employeeCommands = new SqliteEmployeeCommandPort(connectionString);
		var readiness = new SqliteReadinessQueryPort(connectionString);
		var browse = new SqliteJobBrowseQueryPort(connectionString);
		var awaitingProgress = new SqliteAwaitingProgressQueryPort(connectionString);
		var jobs = new SqliteJobNodeCommandPort(connectionString);
		var sessions = new SqliteWorkSessionCommandPort(connectionString);
		var leafSessions = new SqliteWorkSessionQueryPort(connectionString);
		var leafWork = new SqliteLeafWorkQueryPort(connectionString);
		var prerequisites = new SqlitePrerequisiteQueryPort(connectionString);
		var scheduleQueries = new SqliteScheduleQueryPort(connectionString);
		var achievements = new SqliteAchievementCommandPort(connectionString);
		var schedules = new SqliteScheduleCommandPort(connectionString);
		var rates = new SqliteRateCommandPort(connectionString);
		var rateQueries = new SqliteRateQueryPort(connectionString);
		var costs = new SqliteCostQueryPort(connectionString);
		var audit = new SqliteAuditQueryPort(connectionString);
		var tokens = new SqlitePersonalAccessTokenPort(connectionString);
		var requests = new SqliteJobRequestCommandPort(connectionString);
		var authenticationAudit = new SqliteAuthenticationAuditPort(connectionString);
		var credentials = new SqliteAccountCredentialPort(connectionString);
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
