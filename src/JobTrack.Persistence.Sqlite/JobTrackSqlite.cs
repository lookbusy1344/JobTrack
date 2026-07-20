namespace JobTrack.Persistence.Sqlite;

using Application;
using Microsoft.AspNetCore.Identity;
using NodaTime;

/// <summary>Composes the SQLite provider behind JobTrack's single public facade.</summary>
public static class JobTrackSqlite
{
	/// <summary>Creates a provider-neutral client over the configured SQLite database.</summary>
	public static IJobTrackClient Create(
		string connectionString,
		IPasswordHasher<BootstrapCredentialSubject>? passwordHasher = null,
		IPasswordHasher<EmployeeCredentialSubject>? employeePasswordHasher = null,
		IClock? clock = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

		clock ??= SystemClock.Instance;

		var bootstrap = new SqliteInstallationBootstrapPort(connectionString, clock);
		var employees = new SqliteEmployeeQueryPort(connectionString, clock);
		var employeeCommands = new SqliteEmployeeCommandPort(connectionString, clock);
		var readiness = new SqliteReadinessQueryPort(connectionString);
		var browse = new SqliteJobBrowseQueryPort(connectionString);
		var awaitingProgress = new SqliteAwaitingProgressQueryPort(connectionString);
		var jobs = new SqliteJobNodeCommandPort(connectionString, clock);
		var sessions = new SqliteWorkSessionCommandPort(connectionString, clock);
		var leafSessions = new SqliteWorkSessionQueryPort(connectionString, clock);
		var leafWork = new SqliteLeafWorkQueryPort(connectionString);
		var prerequisites = new SqlitePrerequisiteQueryPort(connectionString);
		var scheduleQueries = new SqliteScheduleQueryPort(connectionString, clock);
		var achievements = new SqliteAchievementCommandPort(connectionString, clock);
		var schedules = new SqliteScheduleCommandPort(connectionString, clock);
		var rates = new SqliteRateCommandPort(connectionString, clock);
		var rateQueries = new SqliteRateQueryPort(connectionString, clock);
		var costs = new SqliteCostQueryPort(connectionString, clock);
		var audit = new SqliteAuditQueryPort(connectionString, clock);
		var tokens = new SqlitePersonalAccessTokenPort(connectionString, clock);
		var requests = new SqliteJobRequestCommandPort(connectionString, clock);
		var authenticationAudit = new SqliteAuthenticationAuditPort(connectionString, clock);
		var credentials = new SqliteAccountCredentialPort(connectionString, clock);
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
