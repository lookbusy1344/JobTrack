namespace JobTrack.Persistence.PostgreSql;

using Application;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using Npgsql;
using Shared.Ports;

/// <summary>Composes the PostgreSQL provider behind JobTrack's single public facade.</summary>
public static class JobTrackPostgreSql
{
	/// <summary>Creates a provider-neutral client over one shared pooled data source, with default password hashing and clock.</summary>
	/// <remarks>
	///     The simple overload for the common case; see
	///     <see
	///         cref="Create(NpgsqlDataSource, Microsoft.AspNetCore.Identity.IPasswordHasher{JobTrack.Application.BootstrapCredentialSubject}?, IPasswordHasher{EmployeeCredentialSubject}?, IClock?)" />
	///     to customize password hashing or the clock, or
	///     <see
	///         cref="CreateWithPatDataSources(NpgsqlDataSource, NpgsqlDataSource, NpgsqlDataSource, IPasswordHasher{BootstrapCredentialSubject}?, IPasswordHasher{EmployeeCredentialSubject}?, IClock?)" />
	///     for production PostgreSQL role separation.
	/// </remarks>
	[CLSCompliant(false)]
	public static IJobTrackClient Create(NpgsqlDataSource dataSource) => Create(dataSource, null);

	/// <summary>Creates a provider-neutral client over one shared pooled data source.</summary>
	/// <remarks>
	///     For production PostgreSQL role separation, prefer
	///     <see
	///         cref="CreateWithPatDataSources(NpgsqlDataSource, NpgsqlDataSource, NpgsqlDataSource, IPasswordHasher{BootstrapCredentialSubject}?, IPasswordHasher{EmployeeCredentialSubject}?, IClock?)" />
	///     .
	///     This convenience member is intended for SQLite-like single-credential development/test
	///     installations and delegates all PAT operations to <paramref name="dataSource" />.
	///     Marked not CLS-compliant because its parameter types come from dependencies that do not
	///     declare compliance themselves (ASP.NET Core Identity's <c>IPasswordHasher{T}</c> and Npgsql). The
	///     assembly's own surface is compliant; this is the one member that cannot be.
	/// </remarks>
	[CLSCompliant(false)]
	public static IJobTrackClient Create(
		NpgsqlDataSource dataSource,
		IPasswordHasher<BootstrapCredentialSubject>? passwordHasher = null,
		IPasswordHasher<EmployeeCredentialSubject>? employeePasswordHasher = null,
		IClock? clock = null) =>
		CreateWithPatDataSources(dataSource, dataSource, dataSource, passwordHasher, employeePasswordHasher, clock);

	/// <summary>
	///     Creates a provider-neutral client with distinct least-privilege PAT management and
	///     authentication connections, with default password hashing and clock.
	/// </summary>
	/// <remarks>
	///     The simple overload for the common case; see
	///     <see
	///         cref="CreateWithPatDataSources(NpgsqlDataSource, NpgsqlDataSource, NpgsqlDataSource, IPasswordHasher{BootstrapCredentialSubject}?, IPasswordHasher{EmployeeCredentialSubject}?, IClock?)" />
	///     to customize password hashing or the clock.
	/// </remarks>
	[CLSCompliant(false)]
	public static IJobTrackClient CreateWithPatDataSources(
		NpgsqlDataSource dataSource,
		NpgsqlDataSource personalAccessTokenManagementDataSource,
		NpgsqlDataSource personalAccessTokenAuthenticationDataSource) =>
		CreateWithPatDataSources(
			dataSource, personalAccessTokenManagementDataSource, personalAccessTokenAuthenticationDataSource, null);

	/// <summary>Creates a provider-neutral client with distinct least-privilege PAT management and authentication connections.</summary>
	[CLSCompliant(false)]
	public static IJobTrackClient CreateWithPatDataSources(
		NpgsqlDataSource dataSource,
		NpgsqlDataSource personalAccessTokenManagementDataSource,
		NpgsqlDataSource personalAccessTokenAuthenticationDataSource,
		IPasswordHasher<BootstrapCredentialSubject>? passwordHasher = null,
		IPasswordHasher<EmployeeCredentialSubject>? employeePasswordHasher = null,
		IClock? clock = null)
	{
		ArgumentNullException.ThrowIfNull(dataSource);
		ArgumentNullException.ThrowIfNull(personalAccessTokenManagementDataSource);
		ArgumentNullException.ThrowIfNull(personalAccessTokenAuthenticationDataSource);

		clock ??= SystemClock.Instance;

		var readOperations = new PostgreSqlReadOperations(dataSource);
		var writeOperations = new PostgreSqlWriteOperations(dataSource);
		var bootstrap = new PostgreSqlInstallationBootstrapPort(dataSource, clock);
		var employees = new EmployeeQueryPort(readOperations, clock);
		var employeeCommands = new EmployeeCommandPort(writeOperations, clock);
		var readiness = new PostgreSqlReadinessQueryPort(dataSource);
		var browse = new JobBrowseQueryPort(new PostgreSqlJobBrowseOperations(dataSource));
		var awaitingProgress = new PostgreSqlAwaitingProgressQueryPort(dataSource);
		var jobs = new PostgreSqlJobNodeCommandPort(dataSource, clock);
		var sessions = new WorkSessionCommandPort(writeOperations, clock);
		var leafSessions = new WorkSessionQueryPort(new PostgreSqlWorkSessionQueryOperations(dataSource), clock);
		var leafWork = new LeafWorkQueryPort(readOperations);
		var prerequisites = new PrerequisiteQueryPort(new PostgreSqlPrerequisiteOperations(dataSource));
		var scheduleQueries = new ScheduleQueryPort(readOperations, clock);
		var achievements = new PostgreSqlAchievementCommandPort(dataSource, clock);
		var schedules = new ScheduleCommandPort(writeOperations, clock);
		var rates = new RateCommandPort(writeOperations, clock);
		var rateQueries = new RateQueryPort(readOperations, clock);
		var costs = new PostgreSqlCostQueryPort(dataSource, clock);
		var audit = new AuditQueryPort(readOperations, clock);
		var tokens = new PostgreSqlPersonalAccessTokenPort(
			personalAccessTokenManagementDataSource, personalAccessTokenAuthenticationDataSource, clock);
		var requests = new PostgreSqlJobRequestCommandPort(dataSource, clock);
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
