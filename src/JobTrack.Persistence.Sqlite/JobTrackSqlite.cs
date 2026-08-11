namespace JobTrack.Persistence.Sqlite;

using Application;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using Shared.Ports;

/// <summary>Composes the SQLite provider behind JobTrack's single public facade.</summary>
public static class JobTrackSqlite
{
	/// <summary>Creates a provider-neutral client over the configured SQLite database with default password hashing and clock.</summary>
	/// <remarks>
	///     The simple overload for the common case; see
	///     <see
	///         cref="Create(string, Microsoft.AspNetCore.Identity.IPasswordHasher{JobTrack.Application.BootstrapCredentialSubject}?, Microsoft.AspNetCore.Identity.IPasswordHasher{JobTrack.Application.EmployeeCredentialSubject}?, IClock?)" />
	///     to customize password hashing or the clock.
	/// </remarks>
	public static IJobTrackClient Create(string connectionString) => Create(connectionString, null);

	/// <summary>Creates a provider-neutral client over the configured SQLite database.</summary>
	/// <remarks>
	///     Marked not CLS-compliant because its parameter types come from dependencies that do not
	///     declare compliance themselves (ASP.NET Core Identity's <c>IPasswordHasher{T}</c>). The
	///     assembly's own surface is compliant; this is the one member that cannot be.
	/// </remarks>
	[CLSCompliant(false)]
	public static IJobTrackClient Create(
		string connectionString,
		IPasswordHasher<BootstrapCredentialSubject>? passwordHasher = null,
		IPasswordHasher<EmployeeCredentialSubject>? employeePasswordHasher = null,
		IClock? clock = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

		clock ??= SystemClock.Instance;

		var readOperations = new SqliteReadOperations(connectionString);
		var writeOperations = new SqliteWriteOperations(connectionString);
		var bootstrap = new SqliteInstallationBootstrapPort(connectionString, clock);
		var employees = new EmployeeQueryPort(readOperations, clock);
		var employeeCommands = new EmployeeCommandPort(writeOperations, clock);
		var readiness = new SqliteReadinessQueryPort(connectionString);
		var browse = new JobBrowseQueryPort(new SqliteJobBrowseOperations(connectionString));
		var awaitingProgress = new SqliteAwaitingProgressQueryPort(connectionString);
		var jobs = new SqliteJobNodeCommandPort(connectionString, clock);
		var sessions = new WorkSessionCommandPort(writeOperations, clock);
		var leafSessions = new WorkSessionQueryPort(new SqliteWorkSessionQueryOperations(connectionString), clock);
		var leafWork = new LeafWorkQueryPort(readOperations);
		var prerequisites = new PrerequisiteQueryPort(new SqlitePrerequisiteOperations(connectionString));
		var scheduleQueries = new ScheduleQueryPort(readOperations, clock);
		var achievements = new SqliteAchievementCommandPort(connectionString, clock);
		var schedules = new ScheduleCommandPort(writeOperations, clock);
		var rates = new RateCommandPort(writeOperations, clock);
		var rateQueries = new RateQueryPort(readOperations, clock);
		var costs = new SqliteCostQueryPort(connectionString, clock);
		var audit = new AuditQueryPort(readOperations, clock);
		var tokens = new SqlitePersonalAccessTokenPort(connectionString, clock);
		var requests = new SqliteJobRequestCommandPort(connectionString, clock);
		var authenticationAudit = new AuthenticationAuditPort(writeOperations, clock);
		var credentials = new AccountCredentialPort(
			writeOperations, clock, employeePasswordHasher ?? new PasswordHasher<EmployeeCredentialSubject>());
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
			new RequestCommands(requests, costQueries, readiness, clock),
			new AuthenticationAuditCommands(authenticationAudit),
			new AccountCredentialCommands(credentials));
	}
}
