namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Hierarchy;

/// <summary>
///     Shared contract for <see cref="IJobBrowseQueryPort" /> (plan §8.5 slice 2), asserted identically
///     against PostgreSQL and SQLite by one thin sealed subclass per provider's own test project --
///     same shape as <see cref="EmployeeQueryPortContractTestsBase" />/<see cref="JobNodeCommandPortContractTestsBase" />.
///     Seeds a small tree via the real <see cref="IInstallationBootstrapPort" />/<see cref="IJobNodeCommandPort" />,
///     not hand-rolled SQL, except for the second employee row (no employee-creation port exists at
///     this layer, so it's seeded the same way <see cref="JobNodeCommandPortContractTestsBase" /> seeds
///     its worker).
/// </summary>
public abstract class JobBrowseQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected JobBrowseQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetNodeAsync_with_null_returns_the_root_with_no_ancestors()
	{
		var (rootId, _, _) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.GetNodeAsync(null);

		result.Node.Id.Should().Be(rootId);
		result.Node.ParentId.Should().BeNull();
		result.Ancestors.Should().BeEmpty();
	}

	[Fact]
	public async Task GetNodeAsync_returns_ordered_ancestry_for_a_deep_node()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.GetNodeAsync(tree.CabinetsLeafId);

		result.Node.Id.Should().Be(tree.CabinetsLeafId);
		result.Ancestors.Select(a => a.Id).Should().ContainInOrder(rootId, branchId);
	}

	[Fact]
	public async Task GetNodeAsync_throws_for_a_nonexistent_node()
	{
		var (rootId, _, _) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var act = () => port.GetNodeAsync(new JobNodeId(rootId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetChildrenAsync_returns_only_direct_children()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var rootChildren = await port.GetChildrenAsync(rootId, OwnershipFilter.All, JobArchiveFilter.All);

		rootChildren.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(branchId);
	}

	[Fact]
	public async Task GetChildrenAsync_throws_for_a_nonexistent_parent()
	{
		var (rootId, _, _) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var act = () => port.GetChildrenAsync(new(rootId.Value + 999), OwnershipFilter.All, JobArchiveFilter.All);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetChildrenAsync_filters_by_owner()
	{
		var (_, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var ownedByJobManager = await port.GetChildrenAsync(branchId, OwnershipFilter.OwnedBy(tree.JobManagerId), JobArchiveFilter.All);

		ownedByJobManager.Select(c => c.Id).Should().BeEquivalentTo([tree.CabinetsLeafId, tree.OldWiringLeafId]);
	}

	[Fact]
	public async Task GetChildrenAsync_unassigned_filter_returns_only_unowned_children_without_throwing()
	{
		var (_, branchId, tree) = await SeedTreeAsync();
		var commandPort = CreateCommandPort(database.ConnectionString);
		var unassigned = await commandPort.AddChildAsync(new() {
			Context = ContextFor(tree.JobManagerId),
			ParentId = branchId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Low,
		});
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.GetChildrenAsync(branchId, OwnershipFilter.Unassigned, JobArchiveFilter.All);

		result.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(unassigned.Id);
		result.Single().OwnerUserId.Should().BeNull();
	}

	[Fact]
	public async Task GetChildrenAsync_respects_archive_filter_tri_state()
	{
		var (_, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var active = await port.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.ActiveOnly);
		var archived = await port.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.ArchivedOnly);
		var all = await port.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.All);

		active.Select(c => c.Id).Should().BeEquivalentTo([tree.CabinetsLeafId, tree.PlumbingLeafId]);
		archived.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(tree.OldWiringLeafId);
		all.Should().HaveCount(3);
	}

	[Fact]
	public async Task GetChildrenAsync_flags_HasChildren_correctly()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var rootChildren = await port.GetChildrenAsync(rootId, OwnershipFilter.All, JobArchiveFilter.All);
		var branchChildren = await port.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.All);

		rootChildren.Should().ContainSingle(c => c.Id == branchId && c.HasChildren);
		branchChildren.Should().OnlyContain(c => !c.HasChildren);
	}

	[Fact]
	public async Task Parent_returns_as_Branch_immediately_after_child_is_added_under_childless_node()
	{
		var (rootId, _, tree) = await SeedTreeAsync();
		var commandPort = CreateCommandPort(database.ConnectionString);
		var browsePort = CreateBrowsePort(database.ConnectionString);

		var childless = await commandPort.AddChildAsync(new() {
			Context = ContextFor(tree.JobManagerId),
			ParentId = rootId,
			Description = "Childless until child added",
			OwnerUserId = tree.JobManagerId,
			Priority = Priority.Medium,
		});

		var beforeChild = await browsePort.GetNodeAsync(childless.Id);
		beforeChild.Node.Kind.Should().Be(NodeKind.Leaf);
		beforeChild.Node.HasChildren.Should().BeFalse();

		_ = await commandPort.AddChildAsync(new() {
			Context = ContextFor(tree.JobManagerId),
			ParentId = childless.Id,
			Description = "First child",
			OwnerUserId = tree.JobManagerId,
			Priority = Priority.Medium,
		});

		var parentAfter = await browsePort.GetNodeAsync(childless.Id);
		parentAfter.Node.Kind.Should().Be(NodeKind.Branch);
		parentAfter.Node.HasChildren.Should().BeTrue();

		var rootChildren = await browsePort.GetChildrenAsync(rootId, OwnershipFilter.All, JobArchiveFilter.All);
		rootChildren.Should().Contain(c => c.Id == childless.Id && c.Kind == NodeKind.Branch && c.HasChildren);
	}

	[Fact]
	public async Task GetChildrenAsync_bounds_results_by_offset_and_limit_ordered_deterministically_by_id()
	{
		var (_, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var all = await port.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.All);
		var orderedIds = all.Select(c => c.Id).OrderBy(id => id.Value).ToArray();

		var firstPage = await port.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.All, 0, 2);
		var secondPage = await port.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.All, 2, 2);

		firstPage.Select(c => c.Id).Should().ContainInOrder(orderedIds.Take(2));
		secondPage.Select(c => c.Id).Should().ContainInOrder(orderedIds.Skip(2));
	}

	[Fact]
	public async Task SearchJobNodesAsync_matches_substring_case_insensitively()
	{
		var (_, _, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.SearchJobNodesAsync("CABINETS", OwnershipFilter.All, JobArchiveFilter.All);

		result.Select(r => r.Id).Should().ContainSingle().Which.Should().Be(tree.CabinetsLeafId);
	}

	[Fact]
	public async Task SearchJobNodesAsync_applies_owner_and_archive_filters()
	{
		var (_, _, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var ownedByWorker = await port.SearchJobNodesAsync("install", OwnershipFilter.OwnedBy(tree.WorkerId), JobArchiveFilter.All);
		var activeOnly = await port.SearchJobNodesAsync("job", OwnershipFilter.All, JobArchiveFilter.ActiveOnly);

		ownedByWorker.Select(r => r.Id).Should().ContainSingle().Which.Should().Be(tree.PlumbingLeafId);
		activeOnly.Should().NotContain(r => r.Id == tree.OldWiringLeafId);
	}

	[Fact]
	public async Task GetSummariesByIdsAsync_returns_matching_summaries_including_archived_nodes()
	{
		var (_, _, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		EquatableArray<JobNodeId> ids = [tree.CabinetsLeafId, tree.OldWiringLeafId];
		var result = await port.GetSummariesByIdsAsync(ids);

		result.Select(r => r.Id).Should().BeEquivalentTo([tree.CabinetsLeafId, tree.OldWiringLeafId]);
		result.Should().ContainSingle(r => r.Id == tree.OldWiringLeafId && r.ArchivedAt != null);
	}

	[Fact]
	public async Task GetSummariesByIdsAsync_silently_omits_unknown_ids()
	{
		var (rootId, _, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		EquatableArray<JobNodeId> ids = [tree.CabinetsLeafId, new(rootId.Value + 999)];
		var result = await port.GetSummariesByIdsAsync(ids);

		result.Select(r => r.Id).Should().ContainSingle().Which.Should().Be(tree.CabinetsLeafId);
	}

	[Fact]
	public async Task GetSummariesByIdsAsync_returns_empty_for_empty_input()
	{
		_ = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.GetSummariesByIdsAsync([]);

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetSubtreeAsync_returns_every_node_within_the_requested_depth()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.GetSubtreeAsync(rootId, 2, OwnershipFilter.All, JobArchiveFilter.All);

		result.Select(r => r.Id).Should().BeEquivalentTo(
			[rootId, branchId, tree.CabinetsLeafId, tree.PlumbingLeafId, tree.OldWiringLeafId]);
		result.Should().ContainSingle(r => r.Id == rootId && r.Depth == 0);
		result.Should().ContainSingle(r => r.Id == branchId && r.Depth == 1);
		result.Should().ContainSingle(r => r.Id == tree.CabinetsLeafId && r.Depth == 2);
	}

	[Fact]
	public async Task GetSubtreeAsync_stops_recursion_beyond_maxDepth_and_flags_HasUnexpandedChildren()
	{
		var (rootId, branchId, _) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.GetSubtreeAsync(rootId, 1, OwnershipFilter.All, JobArchiveFilter.All);

		result.Select(r => r.Id).Should().BeEquivalentTo([rootId, branchId]);
		result.Should().ContainSingle(r => r.Id == branchId && r.HasChildren && r.HasUnexpandedChildren);
	}

	[Fact]
	public async Task GetSubtreeAsync_throws_for_a_nonexistent_root()
	{
		var (rootId, _, _) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var act = () => port.GetSubtreeAsync(new(rootId.Value + 999), 2, OwnershipFilter.All, JobArchiveFilter.All);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(JobSubtreeLimits.HardMaxDepth + 1)]
	public async Task GetSubtreeAsync_throws_for_maxDepth_outside_the_contract_bounds(int maxDepth)
	{
		var (rootId, _, _) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var act = () => port.GetSubtreeAsync(rootId, maxDepth, OwnershipFilter.All, JobArchiveFilter.All);

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task GetSubtreeAsync_never_caps_the_root_s_immediate_children()
	{
		var (rootId, _, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);
		var commandPort = CreateCommandPort(database.ConnectionString);

		var wideChildren = new List<JobNodeId>();
		for (var i = 0; i < JobSubtreeLimits.BreadthCap + 5; i++) {
			var child = await commandPort.AddChildAsync(new() {
				Context = ContextFor(tree.JobManagerId),
				ParentId = rootId,
				Description = $"Wide child {i}",
				OwnerUserId = tree.JobManagerId,
				Priority = Priority.Low,
			});
			wideChildren.Add(child.Id);
		}

		var result = await port.GetSubtreeAsync(rootId, 1, OwnershipFilter.All, JobArchiveFilter.All);

		result.Select(r => r.Id).Should().Contain(wideChildren);
	}

	[Fact]
	public async Task GetSubtreeAsync_caps_recursion_at_the_breadth_cap_for_non_root_parents()
	{
		var (rootId, _, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);
		var commandPort = CreateCommandPort(database.ConnectionString);

		var parent = await commandPort.AddChildAsync(new() {
			Context = ContextFor(tree.JobManagerId),
			ParentId = rootId,
			Description = "Wide parent",
			OwnerUserId = tree.JobManagerId,
			Priority = Priority.Low,
		});

		var childIds = new List<JobNodeId>();
		for (var i = 0; i < JobSubtreeLimits.BreadthCap + 5; i++) {
			var child = await commandPort.AddChildAsync(new() {
				Context = ContextFor(tree.JobManagerId),
				ParentId = parent.Id,
				Description = $"Wide grandchild parent {i}",
				OwnerUserId = tree.JobManagerId,
				Priority = Priority.Low,
			});
			childIds.Add(child.Id);
			_ = await commandPort.AddChildAsync(new() {
				Context = ContextFor(tree.JobManagerId),
				ParentId = child.Id,
				Description = $"Great-grandchild {i}",
				OwnerUserId = tree.JobManagerId,
				Priority = Priority.Low,
			});
		}

		var result = await port.GetSubtreeAsync(rootId, 3, OwnershipFilter.All, JobArchiveFilter.All);

		var childRows = childIds.Select(id => result.Single(r => r.Id == id)).OrderBy(r => r.Id.Value).ToList();
		childRows.Take(JobSubtreeLimits.BreadthCap).Should().OnlyContain(r => !r.HasUnexpandedChildren);
		childRows.Skip(JobSubtreeLimits.BreadthCap).Should().OnlyContain(r => r.HasUnexpandedChildren);

		var expandedGreatGrandchildCount = result.Count(r => r.Depth == 3);
		expandedGreatGrandchildCount.Should().Be(JobSubtreeLimits.BreadthCap);
	}

	[Fact]
	public async Task GetSubtreeAsync_applies_structural_pass_through_filtering()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var port = CreateBrowsePort(database.ConnectionString);

		var result = await port.GetSubtreeAsync(rootId, 2, OwnershipFilter.OwnedBy(tree.WorkerId), JobArchiveFilter.All);

		result.Select(r => r.Id).Should().BeEquivalentTo([rootId, branchId, tree.PlumbingLeafId]);
		result.Should().ContainSingle(r => r.Id == tree.PlumbingLeafId && r.MatchesFilter);
		result.Should().ContainSingle(r => r.Id == rootId && !r.MatchesFilter);
		result.Should().ContainSingle(r => r.Id == branchId && !r.MatchesFilter);
	}

	/// <summary>
	///     ADR 0039's correction: <c>job_node</c> is adjacency-list, not nested-set, so there is no
	///     stored <c>(lft, rgt)</c> to tear -- the property under test is that a subtree fetch racing a
	///     concurrent move returns a coherent snapshot of <c>parent_id</c> edges (unique ids, and every
	///     row's depth consistent with its parent's depth when the parent is also in the result), not
	///     that the two operations serialize.
	/// </summary>
	[Fact]
	public async Task GetSubtreeAsync_returns_a_coherent_snapshot_during_a_concurrent_move()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var commandPort = CreateCommandPort(database.ConnectionString);
		var browsePort = CreateBrowsePort(database.ConnectionString);

		var otherParent = await commandPort.AddChildAsync(new() {
			Context = ContextFor(tree.JobManagerId),
			ParentId = rootId,
			Description = "Alternate parent",
			OwnerUserId = tree.JobManagerId,
			Priority = Priority.Low,
		});
		var branchDetail = await browsePort.GetNodeAsync(branchId);

		var moveTask = commandPort.MoveAsync(new() {
			Context = ContextFor(tree.JobManagerId),
			NodeId = branchId,
			NewParentId = otherParent.Id,
			Version = branchDetail.Node.Version,
		});
		var fetchTask = browsePort.GetSubtreeAsync(rootId, 2, OwnershipFilter.All, JobArchiveFilter.All);

		await Task.WhenAll(moveTask, fetchTask);
		var result = await fetchTask;

		result.Select(r => r.Id).Should().OnlyHaveUniqueItems();
		foreach (var row in result.Where(r => r.Depth > 0)) {
			var parentInResult = result.FirstOrDefault(r => r.Id == row.ParentId);
			if (parentInResult is not null) {
				row.Depth.Should().Be(parentInResult.Depth + 1);
			}
		}
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateCommandPort(string connectionString);

	protected abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	protected abstract IJobBrowseQueryPort CreateBrowsePort(string connectionString);

	/// <summary>Stage 6 efficiency-guard seam: a browse port wired with <paramref name="interceptor" /> attached to its <c>DbContext</c>.</summary>
	protected abstract IJobBrowseQueryPort CreateBrowsePortWithCommandCounter(string connectionString, CommandCountInterceptor interceptor);

	/// <summary>
	///     Stage 6 (2026-07-15 plan §5): a subtree fetch is two SQL round trips -- the bounded recursive
	///     fetch and the shaped-detail fetch -- never more, regardless of how many siblings a level has.
	///     Seeds well past <see cref="JobSubtreeLimits.BreadthCap" /> to prove the round-trip count does
	///     not scale with row count (no N+1).
	/// </summary>
	[Fact]
	public async Task GetSubtreeAsync_executes_a_fixed_number_of_round_trips_regardless_of_subtree_width()
	{
		var (rootId, _, tree) = await SeedTreeAsync();
		var commandPort = CreateCommandPort(database.ConnectionString);
		for (var i = 0; i < JobSubtreeLimits.BreadthCap + 10; i++) {
			_ = await commandPort.AddChildAsync(new() {
				Context = ContextFor(tree.JobManagerId),
				ParentId = rootId,
				Description = $"Wide sibling {i}",
				OwnerUserId = tree.JobManagerId,
				Priority = Priority.Low,
			});
		}

		var interceptor = new CommandCountInterceptor();
		var port = CreateBrowsePortWithCommandCounter(database.ConnectionString, interceptor);

		_ = await port.GetSubtreeAsync(rootId, 2, OwnershipFilter.All, JobArchiveFilter.All);

		interceptor.Count.Should().Be(
			3, "a root-existence check, the bounded recursive fetch, and the shaped-detail fetch -- fixed regardless of subtree width (no N+1)");
	}

	/// <summary>
	///     A browse row carries the leaf's own achievement, so a caller can mark job state per row
	///     without a follow-up query per row. <see langword="null" /> means no <c>leaf_work</c> is
	///     attached yet -- structurally distinct from <see cref="Achievement.Waiting" />, which is
	///     attached work nobody has started. A branch has no achievement of its own either way.
	/// </summary>
	[Fact]
	public async Task GetChildrenAsync_reports_each_leafs_achievement()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var browsePort = CreateBrowsePort(database.ConnectionString);

		await AdvancePlumbingToInProgressAsync(tree);

		var children = await browsePort.GetChildrenAsync(branchId, OwnershipFilter.All, JobArchiveFilter.All);

		children.Single(c => c.Id == tree.PlumbingLeafId).Achievement.Should().Be(Achievement.InProgress);
		children.Single(c => c.Id == tree.CabinetsLeafId).Achievement.Should().Be(Achievement.Waiting);
		children.Single(c => c.Id == tree.OldWiringLeafId).Achievement.Should().BeNull();

		var rootChildren = await browsePort.GetChildrenAsync(rootId, OwnershipFilter.All, JobArchiveFilter.All);
		rootChildren.Single(c => c.Id == branchId).Achievement.Should().BeNull();
	}

	[Fact]
	public async Task SearchJobNodesAsync_reports_each_leafs_achievement()
	{
		var (_, _, tree) = await SeedTreeAsync();
		var browsePort = CreateBrowsePort(database.ConnectionString);

		await AdvancePlumbingToInProgressAsync(tree);

		var result = await browsePort.SearchJobNodesAsync("Install", OwnershipFilter.All, JobArchiveFilter.All);

		result.Single(r => r.Id == tree.PlumbingLeafId).Achievement.Should().Be(Achievement.InProgress);
		result.Single(r => r.Id == tree.CabinetsLeafId).Achievement.Should().Be(Achievement.Waiting);
	}

	[Fact]
	public async Task GetSubtreeAsync_reports_each_leafs_achievement()
	{
		var (rootId, branchId, tree) = await SeedTreeAsync();
		var browsePort = CreateBrowsePort(database.ConnectionString);

		await AdvancePlumbingToInProgressAsync(tree);

		var rows = await browsePort.GetSubtreeAsync(rootId, JobSubtreeLimits.HardMaxDepth, OwnershipFilter.All, JobArchiveFilter.All);

		rows.Single(r => r.Id == tree.PlumbingLeafId).Achievement.Should().Be(Achievement.InProgress);
		rows.Single(r => r.Id == tree.CabinetsLeafId).Achievement.Should().Be(Achievement.Waiting);
		rows.Single(r => r.Id == tree.OldWiringLeafId).Achievement.Should().BeNull();
		rows.Single(r => r.Id == branchId).Achievement.Should().BeNull();
	}

	/// <summary>
	///     Attaches leaf work to the cabinets leaf (leaving it at <see cref="Achievement.Waiting" />) and
	///     to the plumbing leaf, then advances plumbing to <see cref="Achievement.InProgress" />. The old
	///     wiring leaf is deliberately left without leaf work.
	/// </summary>
	private async Task AdvancePlumbingToInProgressAsync(SeededTree tree)
	{
		var commandPort = CreateCommandPort(database.ConnectionString);
		var achievementPort = CreateAchievementPort(database.ConnectionString);

		_ = await commandPort.AttachLeafWorkAsync(
			new() { Context = ContextFor(tree.JobManagerId), JobNodeId = tree.CabinetsLeafId });

		var plumbingWork = await commandPort.AttachLeafWorkAsync(
			new() { Context = ContextFor(tree.JobManagerId), JobNodeId = tree.PlumbingLeafId });
		_ = await achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(tree.JobManagerId),
			JobNodeId = tree.PlumbingLeafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = plumbingWork.Version,
		});
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	/// <summary>
	///     Seeds root (owned by the bootstrap administrator) -&gt; branch "Kitchen renovation" (owned by
	///     the administrator) -&gt; three leaves: "Install cabinets" (administrator-owned, active),
	///     "Install plumbing" (worker-owned, active), "Old wiring job" (administrator-owned, archived).
	/// </summary>
	private async Task<(JobNodeId RootId, JobNodeId BranchId, SeededTree Tree)> SeedTreeAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
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
		var jobManagerId = bootstrap.AdministratorId;

		await using (var connection = await OpenExistingConnectionAsync()) {
			await AssignRoleAsync(connection, jobManagerId, EmployeeRole.JobManager);
		}

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper", EmployeeRole.Worker);

		var commandPort = CreateCommandPort(database.ConnectionString);
		var branch = await commandPort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = bootstrap.RootJobNodeId,
			Description = "Kitchen renovation",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		});
		var cabinets = await commandPort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Install cabinets",
			OwnerUserId = jobManagerId,
			Priority = Priority.High,
		});
		var plumbing = await commandPort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Install plumbing",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		var oldWiring = await commandPort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = branch.Id,
			Description = "Old wiring job",
			OwnerUserId = jobManagerId,
			Priority = Priority.Low,
		});
		_ = await commandPort.ArchiveAsync(new() { Context = ContextFor(jobManagerId), NodeId = oldWiring.Id, Version = oldWiring.Version });

		var tree = new SeededTree(jobManagerId, workerId, cabinets.Id, plumbing.Id, oldWiring.Id);
		return (bootstrap.RootJobNodeId, branch.Id, tree);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string displayName, string userName, EmployeeRole role)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityUserCommand = connection.CreateCommand();
		identityUserCommand.CommandText = """
										  INSERT INTO identity_user
										  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										  VALUES
										  	(@appUserId, @userName, @normalizedUserName, 'test-hash', @securityStamp,
										  	 @concurrencyStamp, @requiresPasswordChange, @isEnabled, @lockoutEnabled, 0);
										  """;
		AddParameter(identityUserCommand, "@appUserId", appUserId.Value);
		AddParameter(identityUserCommand, "@userName", userName);
		AddParameter(identityUserCommand, "@normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@requiresPasswordChange", false);
		AddParameter(identityUserCommand, "@isEnabled", true);
		AddParameter(identityUserCommand, "@lockoutEnabled", true);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await AssignRoleAsync(connection, appUserId, role);

		return appUserId;
	}

	private static async Task AssignRoleAsync(DbConnection connection, AppUserId appUserId, EmployeeRole role)
	{
		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}

	private sealed record SeededTree(
		AppUserId JobManagerId,
		AppUserId WorkerId,
		JobNodeId CabinetsLeafId,
		JobNodeId PlumbingLeafId,
		JobNodeId OldWiringLeafId);
}
