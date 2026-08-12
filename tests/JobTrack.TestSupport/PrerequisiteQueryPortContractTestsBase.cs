namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;

/// <summary>
///     Shared contract for <see cref="IPrerequisiteQueryPort" /> (plan §8.5 slice 5), asserted
///     identically against PostgreSQL and SQLite by one thin sealed subclass per provider's own test
///     project -- same shape as <see cref="JobBrowseQueryPortContractTestsBase" />. Seeds two leaves and
///     a prerequisite edge between them via the real
///     <see cref="IInstallationBootstrapPort" />/<see cref="IJobNodeCommandPort" />.
/// </summary>
public abstract class PrerequisiteQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	/// <summary>The administrator <see cref="SeedEdgeAsync" /> bootstrapped, for tests that act after seeding.</summary>
	private AppUserId seededAdministratorId;

	protected PrerequisiteQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task CountDirectDependentsAsync_counts_only_edges_from_the_required_side()
	{
		var (requiredId, dependentId, unrelatedId) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var requiredCount = await port.CountDirectDependentsAsync(requiredId);
		var dependentCount = await port.CountDirectDependentsAsync(dependentId);
		var unrelatedCount = await port.CountDirectDependentsAsync(unrelatedId);

		requiredCount.Should().Be(1);
		dependentCount.Should().Be(0);
		unrelatedCount.Should().Be(0);
	}

	/// <summary>
	///     The dependent-impact warning behind reopening a successful prerequisite: the page has to say
	///     whether reopening would pull the rug out from under work that is running right now, so the
	///     flag follows an <em>active</em> session on any dependent or a leaf beneath it, not merely the
	///     existence of a dependent.
	/// </summary>
	[Fact]
	public async Task HasActiveDependentWorkAsync_is_true_only_while_a_dependent_holds_an_active_session()
	{
		var (requiredId, dependentId, unrelatedId) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		(await port.HasActiveDependentWorkAsync(requiredId)).Should().BeFalse("no session has started yet");

		// The dependent cannot start until its prerequisite succeeds -- which is exactly the state a
		// reopen would undo, so the scenario has to be built in that order.
		await SucceedAsync(requiredId);
		var sessionPort = CreateWorkSessionPort(database.ConnectionString);
		var session = await sessionPort.StartWorkAsync(new() {
			Context = ContextFor(seededAdministratorId),
			JobNodeId = dependentId,
			WorkedByUserId = seededAdministratorId,
		});

		(await port.HasActiveDependentWorkAsync(requiredId)).Should().BeTrue();
		(await port.HasActiveDependentWorkAsync(dependentId)).Should().BeFalse("the dependent has no dependents of its own");
		(await port.HasActiveDependentWorkAsync(unrelatedId)).Should().BeFalse("no edge reaches the running session");

		_ = await sessionPort.FinishSessionAsync(new() {
			Context = ContextFor(seededAdministratorId),
			SessionId = session.Id,
			Version = session.Version,
		});

		(await port.HasActiveDependentWorkAsync(requiredId)).Should().BeFalse("the session has finished");
	}

	[Fact]
	public async Task HasActiveDependentWorkAsync_throws_for_a_nonexistent_node()
	{
		var (requiredId, _, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.HasActiveDependentWorkAsync(new(requiredId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task CountDirectDependentsAsync_throws_for_a_nonexistent_node()
	{
		var (requiredId, _, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.CountDirectDependentsAsync(new(requiredId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_returns_the_edge_from_the_required_side()
	{
		var (requiredId, dependentId, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetPrerequisitesAsync(requiredId);

		result.Should().ContainSingle(e => e.RequiredJobId == requiredId && e.DependentJobId == dependentId);
	}

	[Fact]
	public async Task GetPrerequisitesAsync_returns_the_edge_from_the_dependent_side()
	{
		var (requiredId, dependentId, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetPrerequisitesAsync(dependentId);

		result.Should().ContainSingle(e => e.RequiredJobId == requiredId && e.DependentJobId == dependentId);
	}

	[Fact]
	public async Task GetPrerequisitesAsync_returns_empty_for_a_node_with_no_edges()
	{
		var (_, _, unrelatedId) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetPrerequisitesAsync(unrelatedId);

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_bounds_results_by_offset_and_limit()
	{
		var (requiredId, _, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var firstPage = await port.GetPrerequisitesAsync(requiredId, 0, 1);
		var secondPage = await port.GetPrerequisitesAsync(requiredId, 1, 1);

		firstPage.Should().ContainSingle();
		secondPage.Should().BeEmpty();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_throws_for_a_nonexistent_node()
	{
		var (requiredId, _, _) = await SeedEdgeAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetPrerequisitesAsync(new(requiredId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobCommandPort(string connectionString);

	internal abstract IPrerequisiteQueryPort CreateQueryPort(string connectionString);

	internal abstract IWorkSessionCommandPort CreateWorkSessionPort(string connectionString);

	internal abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	/// <summary>Attaches LeafWork to a seeded leaf and drives it Waiting -&gt; InProgress -&gt; Success.</summary>
	private async Task SucceedAsync(JobNodeId nodeId)
	{
		var context = ContextFor(seededAdministratorId);
		var attached = await CreateJobCommandPort(database.ConnectionString)
			.AttachLeafWorkAsync(new() {
				Context = context,
				JobNodeId = nodeId,
			});
		var achievementPort = CreateAchievementPort(database.ConnectionString);
		var inProgress = await achievementPort.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = nodeId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = attached.Version,
		});
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = context,
			JobNodeId = nodeId,
			NewAchievement = Achievement.Success,
			Reason = "Done",
			Version = inProgress.Version,
		});
	}

	/// <summary>Seeds two leaves with a prerequisite edge (required -&gt; dependent) and a third, unrelated leaf.</summary>
	private async Task<(JobNodeId RequiredId, JobNodeId DependentId, JobNodeId UnrelatedId)> SeedEdgeAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;
		seededAdministratorId = administratorId;

		var jobCommandPort = CreateJobCommandPort(database.ConnectionString);
		var required = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Pour foundation",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var dependent = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Frame walls",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		var unrelated = await jobCommandPort.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Paint fence",
			OwnerUserId = administratorId,
			Priority = Priority.Medium,
		});
		await jobCommandPort.AddPrerequisiteAsync(new() {
			Context = ContextFor(administratorId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		return (required.Id, dependent.Id, unrelated.Id);
	}
}
