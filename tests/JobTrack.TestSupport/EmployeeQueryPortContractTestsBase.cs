namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     Shared contract for <see cref="IEmployeeQueryPort" /> (impl plan §7.4 step 3, §7.3 slice 2),
///     asserted identically against PostgreSQL and SQLite by one thin sealed subclass per provider's
///     own test project — same shape as <see cref="InstallationBootstrapPortContractTestsBase" />, and
///     reuses each provider's own <see cref="IInstallationBootstrapPort" /> to seed the one row these
///     tests query against, rather than hand-rolled raw SQL.
/// </summary>
public abstract class EmployeeQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected EmployeeQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetEmployeeProfileAsync_returns_the_target_profile_and_actors_roles()
	{
		var administratorId = await SeedAdministratorAsync();
		var queryPort = CreateQueryPort(database.ConnectionString);

		var result = await queryPort.GetEmployeeProfileAsync(administratorId, administratorId);

		result.Profile.Id.Should().Be(administratorId);
		result.Profile.DisplayName.Should().Be("Ada Lovelace");
		result.Profile.IanaTimeZone.Should().Be("Europe/London");
		result.Profile.Version.Should().Be(1);
		result.ActorRoles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetEmployeeProfileAsync_throws_for_a_nonexistent_target()
	{
		var administratorId = await SeedAdministratorAsync();
		var queryPort = CreateQueryPort(database.ConnectionString);

		var act = async () => await queryPort.GetEmployeeProfileAsync(administratorId, new(administratorId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetEmployeeProfileAsync_throws_for_a_nonexistent_actor()
	{
		var administratorId = await SeedAdministratorAsync();
		var queryPort = CreateQueryPort(database.ConnectionString);

		var act = async () => await queryPort.GetEmployeeProfileAsync(new(administratorId.Value + 999), administratorId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	/// <summary>
	///     Remediation plan §2.4: <c>GetActorRolesAsync</c> is the one primitive every
	///     actor-admission check in the application layer relies on, so a disabled actor must be
	///     rejected here rather than only at a caller's own re-check. A locked-out actor shares the
	///     same <c>ActorAccountState.EnsureMayAct</c> code path, so this single representative case
	///     covers both without a second near-identical fixture (remediation plan §2.4 step 1: a table
	///     of representative cases, not one test per superficial variant).
	/// </summary>
	[Fact]
	public async Task GetEmployeeProfileAsync_throws_for_a_disabled_actor()
	{
		var administratorId = await SeedAdministratorAsync();
		var commands = new EmployeeCommands(CreateCommandPort(database.ConnectionString), new PasswordHasher<EmployeeCredentialSubject>());

		var disabledWorker = await commands.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Disabled Worker",
			IanaTimeZone = "Europe/London",
			UserName = "disabled.worker",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});
		_ = await commands.SetEnabledAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = disabledWorker.Id,
			Enabled = false,
		});

		var queryPort = CreateQueryPort(database.ConnectionString);

		var act = async () => await queryPort.GetEmployeeProfileAsync(disabledWorker.Id, administratorId);

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task GetAccountStateAsync_returns_the_target_account_state_and_actors_roles()
	{
		var administratorId = await SeedAdministratorAsync();
		var queryPort = CreateQueryPort(database.ConnectionString);

		var result = await queryPort.GetAccountStateAsync(administratorId, administratorId);

		result.AccountState.Id.Should().Be(administratorId);
		result.AccountState.UserName.Should().Be("ada.lovelace");
		result.AccountState.IsEnabled.Should().BeTrue();
		result.AccountState.RequiresPasswordChange.Should().BeTrue();
		result.AccountState.LockoutEnd.Should().BeNull();
		result.ActorRoles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetAccountStateAsync_throws_for_a_nonexistent_target()
	{
		var administratorId = await SeedAdministratorAsync();
		var queryPort = CreateQueryPort(database.ConnectionString);

		var act = async () => await queryPort.GetAccountStateAsync(administratorId, new(administratorId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetAccountStateAsync_throws_for_a_nonexistent_actor()
	{
		var administratorId = await SeedAdministratorAsync();
		var queryPort = CreateQueryPort(database.ConnectionString);

		var act = async () => await queryPort.GetAccountStateAsync(new(administratorId.Value + 999), administratorId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IEmployeeQueryPort CreateQueryPort(string connectionString);

	internal abstract IEmployeeCommandPort CreateCommandPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	[Fact]
	public async Task GetEmployeeDirectoryAsync_returns_enabled_workflow_employees_only()
	{
		var administratorId = await SeedAdministratorAsync();
		var commands = new EmployeeCommands(CreateCommandPort(database.ConnectionString), new PasswordHasher<EmployeeCredentialSubject>());

		var jobManager = await commands.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Katherine Jones",
			IanaTimeZone = "Europe/London",
			UserName = "katherine.jones",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.JobManager,
		});

		var disabledWorker = await commands.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Disabled Dana",
			IanaTimeZone = "Europe/London",
			UserName = "disabled.dana",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});
		_ = await commands.SetEnabledAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = disabledWorker.Id,
			Enabled = false,
		});

		var requester = await commands.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Requester Rita",
			IanaTimeZone = "Europe/London",
			UserName = "requester.rita",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Requester,
		});

		var requestingWorker = await commands.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Requesting Worker",
			IanaTimeZone = "Europe/London",
			UserName = "requesting.worker",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});
		_ = await commands.AssignRoleAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = requestingWorker.Id,
			Role = EmployeeRole.Requester,
		});

		var queryPort = CreateQueryPort(database.ConnectionString);

		var result = await queryPort.GetEmployeeDirectoryAsync();

		result.Select(entry => entry.Id).Should().Contain([administratorId, jobManager.Id])
			  .And.NotContain([disabledWorker.Id, requester.Id, requestingWorker.Id]);
		result.Should().ContainSingle(entry => entry.Id == administratorId)
			  .Which.Should()
			  .BeEquivalentTo(new EmployeeDirectoryEntry {
				  Id = administratorId,
				  DisplayName = "Ada Lovelace",
				  UserName = "ada.lovelace",
			  });
	}

	[Fact]
	public async Task GetAllEmployeesAsync_returns_every_employee_regardless_of_role_or_enabled_state()
	{
		var administratorId = await SeedAdministratorAsync();
		var commands = new EmployeeCommands(CreateCommandPort(database.ConnectionString), new PasswordHasher<EmployeeCredentialSubject>());

		var disabledAuditor = await commands.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Disabled Auditor Amy",
			IanaTimeZone = "Europe/London",
			UserName = "disabled.auditor.amy",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Auditor,
		});
		_ = await commands.SetEnabledAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = disabledAuditor.Id,
			Enabled = false,
		});

		var requester = await commands.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Requester Rita",
			IanaTimeZone = "Europe/London",
			UserName = "requester.rita",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Requester,
		});

		var queryPort = CreateQueryPort(database.ConnectionString);

		var result = await queryPort.GetAllEmployeesAsync();

		result.Select(entry => entry.Id).Should().Contain([administratorId, disabledAuditor.Id, requester.Id]);
	}

	[Fact]
	public async Task GetEmployeeDirectoryAsync_returns_empty_when_no_workflow_employees_exist()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
			await PostgreSqlTestInfrastructure.EnsureSecurityDefinerFunctionsAsync(connection, Provider);
		}

		var queryPort = CreateQueryPort(database.ConnectionString);

		var result = await queryPort.GetEmployeeDirectoryAsync();

		result.Should().BeEmpty();
	}

	private async Task<AppUserId> SeedAdministratorAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
			await PostgreSqlTestInfrastructure.EnsureSecurityDefinerFunctionsAsync(connection, Provider);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		return result.AdministratorId;
	}
}
