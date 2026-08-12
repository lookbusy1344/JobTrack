namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Schedules;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="ICostQueryPort" /> (impl plan §7.4 step 3, §7.3 slice 10:
///     calculate cost details and hierarchy totals), asserted identically against PostgreSQL and
///     SQLite by one thin sealed subclass per provider's own test project -- same shape as
///     <see cref="ScheduleCommandPortContractTestsBase" />. Exercises the real port through
///     <see
///         cref="CostQueries" />
///     (not called directly), the same way <c>CostQueriesTests</c> exercises the
///     fake port, so a passing run proves the real persistence-materialized inputs reproduce the exact
///     dollar amounts and the ADR 0017 exposure boundary the application-layer contract already
///     establishes. <see cref="IWorkSessionCommandPort.CorrectSessionAsync" /> pins each session to
///     deterministic historical instants -- <see cref="IWorkSessionCommandPort.StartSessionAsync" />
///     itself captures the real clock, which a repeatable cost assertion cannot depend on.
/// </summary>
public abstract class CostQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const int BulkCostMaximumCommandCount = 16;
	private const int SingleNodeCostMaximumCommandCount = 16;

	private readonly IDisposableTestDatabase database;

	protected CostQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	private static Instant At(int hour) => hour == 24 ? Instant.FromUtc(2026, 1, 2, 0, 0) : Instant.FromUtc(2026, 1, 1, hour, 0);

	[Fact]
	public async Task A_cost_viewer_can_calculate_cost_details_for_a_leaf()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		result.NodeId.Should().Be(leafId);
		result.ExactCost.Should().Be(new(120m));
		result.DisplayedCost.Should().Be(new(120m));
		result.AllocatedDuration.ToHours().Should().Be(2m);
		result.Trace.Should().OnlyContain(entry => entry.NodeId == leafId);
	}

	[Fact]
	public async Task A_recurring_schedule_is_expanded_only_across_the_relevant_session_range()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		var schedulePort = CreateSchedulePort(database.ConnectionString);
		_ = await schedulePort.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"],
				new(2026, 1, 1),
				null,
				[new(IsoDayOfWeek.Thursday, new(9, 0), new(17, 0))]),
		});
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		result.ExactCost.Should().Be(new(120m));
	}

	/// <summary>
	///     <c>branchId</c> and the root are administrator-owned (see <c>SeedTreeAsync</c>), so ADR 0040's
	///     ownership carve-out does not apply here -- distinct from
	///     <see cref="A_worker_may_view_cost_details_for_a_node_they_own_despite_no_qualifying_role" />,
	///     which exercises that carve-out on <c>leafId</c>.
	/// </summary>
	[Fact]
	public async Task A_worker_without_cost_viewing_permission_or_ownership_cannot_calculate_cost_details()
	{
		var (_, branchId, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(workerId), NodeId = branchId, AsOf = At(24) });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	/// <summary>
	///     ADR 0040: a worker with no cost-viewing role may still view cost details for a node they own directly (<c>leafId</c>, owned by
	///     <c>workerId</c> per <c>SeedTreeAsync</c>).
	/// </summary>
	[Fact]
	public async Task A_worker_may_view_cost_details_for_a_node_they_own_despite_no_qualifying_role()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(workerId), NodeId = leafId, AsOf = At(24) });

		result.ExactCost.Should().Be(new(120m));
	}

	[Fact]
	public async Task Hierarchy_totals_reflect_a_workers_foreign_concurrent_session_without_exposing_it()
	{
		var (_, branchId, leafId, otherLeafId, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		await CreateCorrectedSessionAsync(administratorId, workerId, otherLeafId, At(10), At(12));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(administratorId), NodeId = branchId, AsOf = At(24) });

		// [09:00,10:00) session1 alone: 1h @ 60 = 60. [10:00,11:00) both sessions share: 0.5h @ 60 = 30. Total 90.
		result.ExactCosts.Should().ContainKeys(branchId, leafId);
		result.ExactCosts.Should().NotContainKey(otherLeafId);
		result.ExactCosts[leafId].Should().Be(new(90m));
		result.ExactCosts[branchId].Should().Be(new(90m));
		result.DisplayedCosts[branchId].Should().Be(new(90m));
		result.DisplayedCosts[leafId].Should().Be(new(90m));
		result.AllocatedDurations[branchId].ToHours().Should().Be(1.5m);
		result.AllocatedDurations[leafId].ToHours().Should().Be(1.5m);
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_prices_every_candidate_from_one_snapshot_matching_individual_hierarchy_totals()
	{
		var (_, branchId, leafId, otherLeafId, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		await CreateCorrectedSessionAsync(administratorId, workerId, otherLeafId, At(10), At(12));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var bulk = await sut.GetBulkNodeCostsAsync(new() {
			Context = ContextFor(administratorId),
			NodeIds = [branchId, leafId, otherLeafId],
			AsOf = At(24),
		});
		var individualBranch = await sut.GetHierarchyTotalsAsync(
			new() { Context = ContextFor(administratorId), NodeId = branchId, AsOf = At(24) });
		var individualOtherLeaf = await sut.GetHierarchyTotalsAsync(
			new() { Context = ContextFor(administratorId), NodeId = otherLeafId, AsOf = At(24) });

		// Same overlap as Hierarchy_totals_reflect_a_workers_foreign_concurrent_session_without_exposing_it:
		// branch/leaf see 90 (the shared [10:00,11:00) segment costed once each side), otherLeaf sees its
		// own contribution only.
		bulk.DisplayedCosts[branchId].Should().Be(individualBranch.DisplayedCosts[branchId]);
		bulk.DisplayedCosts[leafId].Should().Be(new(90m));
		bulk.DisplayedCosts[otherLeafId].Should().Be(individualOtherLeaf.DisplayedCosts[otherLeafId]);
		bulk.AllocatedDurations[branchId].Should().Be(individualBranch.AllocatedDurations[branchId]);
		bulk.AllocatedDurations[leafId].ToHours().Should().Be(1.5m);
		bulk.AllocatedDurations[otherLeafId].Should().Be(individualOtherLeaf.AllocatedDurations[otherLeafId]);
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_omits_a_candidate_the_actor_may_not_view_without_failing_the_rest()
	{
		var (_, branchId, leafId, otherLeafId, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		// leafId is owned by workerId (ADR 0040 admits it); branchId is owned by the administrator, so a
		// plain worker with no cost-viewing role may not see it and otherLeafId does not even exist yet.
		var bulk = await sut.GetBulkNodeCostsAsync(new() { Context = ContextFor(workerId), NodeIds = [branchId, leafId], AsOf = At(24) });

		bulk.DisplayedCosts.Should().NotContainKey(branchId);
		bulk.DisplayedCosts[leafId].Should().Be(new(120m));
		bulk.AllocatedDurations.Should().NotContainKey(branchId);
		bulk.AllocatedDurations[leafId].ToHours().Should().Be(2m);
	}

	/// <summary>
	///     Fresh-eyes review §2.8's own scale check: at the HTTP API's maximum page width
	///     (<c>JobTrackApi.MaxPageSize</c>), bulk pricing must still complete from one connection/snapshot
	///     rather than degrading toward the old one-round-trip-per-row shape. Not a strict latency budget
	///     (§6.5 of performance-budgets.md reserves those for <c>JobTrack.Database.PerformanceTests</c>
	///     against the dedicated scale generator) -- a generous wall-clock ceiling here just catches a
	///     regression back to per-row materialization, which would multiply this by 200.
	/// </summary>
	[Fact]
	public async Task GetBulkNodeCostsAsync_prices_a_maximum_width_page_of_candidates_promptly()
	{
		const int candidateCount = 200;
		var (_, branchId, _, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leafIds = new List<JobNodeId>();
		for (var index = 0; index < candidateCount; ++index) {
			var leaf = await jobNodePort.AddChildAsync(new() {
				Context = ContextFor(administratorId),
				ParentId = branchId,
				Description = $"Scale leaf {index}",
				OwnerUserId = workerId,
				Priority = Priority.Medium,
			});
			_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(administratorId), JobNodeId = leaf.Id });
			await CreateCorrectedSessionAsync(administratorId, workerId, leaf.Id, At(9), At(10));
			leafIds.Add(leaf.Id);
		}

		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var stopwatch = Stopwatch.StartNew();
		var bulk = await sut.GetBulkNodeCostsAsync(new() { Context = ContextFor(administratorId), NodeIds = [.. leafIds], AsOf = At(24) });
		stopwatch.Stop();

		// All 200 sessions are the same worker's, at the identical [09:00,10:00) window, so ADR 0017's
		// concurrency divisor splits that hour's 60-currency cost evenly across all of them: 60 / 200.
		bulk.DisplayedCosts.Should().HaveCount(candidateCount);
		bulk.DisplayedCosts.Should().OnlyContain(entry => entry.Value == new Money(0.30m));
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "bulk pricing must not degrade into one round trip per candidate");
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_keeps_commands_and_connections_constant_at_maximum_width()
	{
		const int candidateCount = 200;
		var (_, branchId, _, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leafIds = new List<JobNodeId>();
		for (var index = 0; index < candidateCount; ++index) {
			var leaf = await jobNodePort.AddChildAsync(new() {
				Context = ContextFor(administratorId),
				ParentId = branchId,
				Description = $"Command-count leaf {index}",
				OwnerUserId = workerId,
				Priority = Priority.Medium,
			});
			_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(administratorId), JobNodeId = leaf.Id });
			await CreateCorrectedSessionAsync(administratorId, workerId, leaf.Id, At(9), At(10));
			leafIds.Add(leaf.Id);
		}

		var narrowCommands = new CommandCountInterceptor();
		var narrowConnections = new ConnectionConcurrencyInterceptor();
		var narrowSut = new CostQueries(CreateCostQueryPortWithInterceptors(
			database.ConnectionString, [narrowCommands, narrowConnections]));
		_ = await narrowSut.GetBulkNodeCostsAsync(
			new() { Context = ContextFor(administratorId), NodeIds = [leafIds[0]], AsOf = At(24) });

		var wideCommands = new CommandCountInterceptor();
		var wideConnections = new ConnectionConcurrencyInterceptor();
		var wideSut = new CostQueries(CreateCostQueryPortWithInterceptors(
			database.ConnectionString, [wideCommands, wideConnections]));
		_ = await wideSut.GetBulkNodeCostsAsync(
			new() { Context = ContextFor(administratorId), NodeIds = [.. leafIds], AsOf = At(24) });

		wideCommands.Count.Should().Be(narrowCommands.Count);
		wideCommands.Count.Should().BeLessThanOrEqualTo(BulkCostMaximumCommandCount);
		narrowConnections.MaximumConcurrentConnections.Should().Be(1);
		wideConnections.MaximumConcurrentConnections.Should().Be(1);
	}

	/// <summary>
	///     2026-08-06-cost-read-materialisation-reduction-plan.md Stage 3: PostgreSQL only (the plan's
	///     own scope -- SQLite's assembly keeps its parameterized recursive-CTE shape unmeasured and
	///     unchanged). The requested subtree's own node ids must never round-trip as an
	///     `= ANY(array)` parameter proportional to subtree size; the fix joins
	///     <c>job_node_subtrees</c>/<c>job_node_ancestor_chains</c> server-side instead. Reuses the same
	///     200-leaf shape as the command-count test above -- an array parameter proportional to subtree
	///     size there would be at least 200 long, versus the requested-root/worker counts this read
	///     actually needs, both far smaller.
	/// </summary>
	[Fact]
	public async Task GetHierarchyTotalsAsync_never_ships_a_node_id_array_proportional_to_subtree_size()
	{
		if (Provider != SchemaProvider.PostgreSql) {
			return;
		}

		const int leafCount = 200;
		var (_, branchId, _, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		for (var index = 0; index < leafCount; ++index) {
			var leaf = await jobNodePort.AddChildAsync(new() {
				Context = ContextFor(administratorId),
				ParentId = branchId,
				Description = $"Array-parameter leaf {index}",
				OwnerUserId = workerId,
				Priority = Priority.Medium,
			});
			_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(administratorId), JobNodeId = leaf.Id });
			await CreateCorrectedSessionAsync(administratorId, workerId, leaf.Id, At(9), At(10));
		}

		var parameters = new MaxArrayParameterLengthInterceptor();
		var sut = new CostQueries(CreateCostQueryPortWithInterceptors(database.ConnectionString, [parameters]));

		_ = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(administratorId), NodeId = branchId, AsOf = At(24) });

		parameters.MaxArrayLength.Should().BeLessThan(
			leafCount, "the requested subtree's own node ids must never round-trip as an array parameter proportional to its size");
	}

	/// <summary>
	///     2026-08-06-cost-read-materialisation-reduction-plan.md Stage 5: PostgreSQL only. Evidence
	///     (a wide-vs-narrow raw read of 36,500 rows at the long-history scale, 50.4 ms vs 9.7 ms)
	///     showed entity materialisation cost visible next to the query itself for
	///     <c>user_schedule_exception</c>, the one table this read loads at meaningful row count
	///     (every other worker-scoped load -- schedule versions/intervals/rates/app users -- stays in
	///     the tens of rows, not worth projecting). The exceptions query now selects only the five
	///     columns <c>CostQueryAssembly</c> reads, never the unused <c>reason</c>/<c>created_by</c>/
	///     <c>changed_at</c>/<c>row_version</c>/<c>id</c> columns a full entity load would carry.
	/// </summary>
	[Fact]
	public async Task GetCostDetailsAsync_projects_schedule_exceptions_to_only_the_columns_it_reads()
	{
		if (Provider != SchemaProvider.PostgreSql) {
			return;
		}

		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));

		var commandTexts = new CommandTextCaptureInterceptor();
		var sut = new CostQueries(CreateCostQueryPortWithInterceptors(database.ConnectionString, [commandTexts]));

		_ = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		var exceptionQuery = commandTexts.CommandTexts.Should()
			.ContainSingle(text => text.Contains("user_schedule_exception", StringComparison.Ordinal)).Subject;
		exceptionQuery.Should().NotContain(
			"reason", "the exceptions query must project only the columns CostQueryAssembly reads, not a full entity");
	}

	/// <summary>
	///     2026-07-25 scalability-follow-up plan §2.4: a single-node cost read's authorization
	///     pre-check (<c>ICostQueryPort.GetCostAccessInputsAsync</c>) and its cost-input materialization
	///     each still open their own connection (they cannot share one transaction/snapshot the way the
	///     bulk path's single call does, since authorization must gate the expensive read), but neither
	///     one is a repeated resource-per-candidate cost, and the actor's roles are resolved exactly
	///     once -- not once for the pre-check and again inside cost-input materialization.
	/// </summary>
	[Fact]
	public async Task GetCostDetailsAsync_keeps_commands_bounded_and_resolves_actor_roles_exactly_once()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));

		var commands = new CommandCountInterceptor();
		var sut = new CostQueries(CreateCostQueryPortWithInterceptors(database.ConnectionString, [commands]));

		_ = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		commands.Count.Should().BeLessThanOrEqualTo(SingleNodeCostMaximumCommandCount);
	}

	/// <summary>
	///     2026-08-06-cost-read-materialisation-reduction-plan.md Stage 2: the earliest-session-start
	///     <c>MIN</c> and the distinct-worker-id list were two sequential queries over the identical
	///     filter (leaf in subtree, started before <c>asOf</c>); one grouped query returns both. Pinned
	///     to the exact post-Stage-2 command count rather than merely below
	///     <see cref="SingleNodeCostMaximumCommandCount" />'s shared upper bound, so a regression that
	///     re-splits the query back into two round trips is caught even though it would still be within
	///     the broader ceiling. Measured pre-Stage-2 count: 14 (both providers) — one query removed
	///     leaves 13.
	/// </summary>
	[Fact]
	public async Task GetCostDetailsAsync_discovers_workers_and_their_earliest_session_in_one_query()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));

		var commands = new CommandCountInterceptor();
		var sut = new CostQueries(CreateCostQueryPortWithInterceptors(database.ConnectionString, [commands]));

		_ = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		commands.Count.Should().Be(13, "worker discovery must issue one grouped query instead of a separate MIN and DISTINCT");
	}

	[Fact]
	public async Task GetCostAccessInputsAsync_reads_roles_and_ancestor_owners_in_one_snapshot()
	{
		var (_, _, leafId, _, administratorId, _) = await SeedTreeAsync();
		var transactions = new TransactionCountInterceptor();
		var port = CreateCostQueryPortWithInterceptors(database.ConnectionString, [transactions]);

		_ = await port.GetCostAccessInputsAsync(administratorId, leafId);

		transactions.Count.Should().Be(1);
	}

	/// <summary>
	///     2026-07-25 scalability-follow-up plan §2.4: authorization must gate the expensive
	///     cost-input materialization -- a denied actor's read touches only the lightweight access-check
	///     connection, never the one that loads worker sessions/schedules/rates, so it must issue
	///     strictly fewer commands than an authorized read against the same tree.
	/// </summary>
	[Fact]
	public async Task GetCostDetailsAsync_denies_authorization_without_opening_the_cost_inputs_connection()
	{
		var (_, branchId, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));

		var deniedCommands = new CommandCountInterceptor();
		var deniedSut = new CostQueries(CreateCostQueryPortWithInterceptors(database.ConnectionString, [deniedCommands]));
		var act = () => deniedSut.GetCostDetailsAsync(new() { Context = ContextFor(workerId), NodeId = branchId, AsOf = At(24) });
		await act.Should().ThrowAsync<AuthorizationDeniedException>();

		var authorizedCommands = new CommandCountInterceptor();
		var authorizedSut = new CostQueries(CreateCostQueryPortWithInterceptors(database.ConnectionString, [authorizedCommands]));
		_ = await authorizedSut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		deniedCommands.Count.Should().BeLessThan(
			authorizedCommands.Count, "an authorization denial must never open the worker-materialization connection");
	}

	[Fact]
	public async Task Calculating_cost_details_for_a_nonexistent_node_throws_not_found()
	{
		var (_, _, _, _, administratorId, _) = await SeedTreeAsync();
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = new(999_999), AsOf = At(24) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetCostDetailsAsync_throws_a_domain_fault_when_a_stored_schedule_zone_id_is_no_longer_recognized()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		var schedulePort = CreateSchedulePort(database.ConnectionString);
		_ = await schedulePort.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"],
				new(2026, 1, 1),
				null,
				[new(IsoDayOfWeek.Thursday, new(0, 0), new(23, 59, 59))]),
		});
		await CorruptStoredScheduleZoneIdAsync(workerId, "Bogus/NotAZone");
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		await act.Should().ThrowAsync<UnknownStoredTimeZoneException>();
	}

	[Fact]
	public async Task A_session_with_no_resolvable_rate_throws_missing_rate()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		await act.Should().ThrowAsync<MissingRateException>();
	}

	/// <summary>
	///     2026-07-24 code-review-scalability-remediation-plan §2.2 step 2: a cost read must not
	///     materialize the whole <c>job_node</c> table. Seeds a decoy subtree of many nodes unrelated to
	///     <c>leafId</c>'s own subtree and asserts none of them appear in the raw port's <c>NodesById</c>.
	///     Also proves the narrowing did not break correctness: a rate override declared on the true
	///     root -- an ancestor <em>above</em> <c>leafId</c>'s own requested subtree, and outside the decoy
	///     subtree entirely -- still resolves (ADR 0040's owner carve-out and
	///     <see cref="Domain.Rates.RateResolver" />'s nearest-ancestor walk both need every requested
	///     root's own path to the true root, not just its descendants).
	/// </summary>
	[Fact]
	public async Task GetCostInputsAsync_excludes_nodes_outside_the_requested_subtree_while_still_resolving_a_true_root_override()
	{
		var (rootId, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await AddNodeRateOverrideAsync(administratorId, workerId, rootId, new(100m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var decoyIds = new List<JobNodeId>();
		for (var index = 0; index < 30; ++index) {
			var decoy = await jobNodePort.AddChildAsync(new() {
				Context = ContextFor(administratorId),
				ParentId = rootId,
				Description = $"Decoy {index}",
				OwnerUserId = administratorId,
				Priority = Priority.Medium,
			});
			decoyIds.Add(decoy.Id);
		}

		var port = CreateCostQueryPort(database.ConnectionString);
		var inputs = await port.GetCostInputsAsync(leafId, At(24), 10_000);

		inputs.NodesById.Keys.Should().NotContain(decoyIds);
		inputs.NodesById.Count.Should().BeLessThan(decoyIds.Count, "the decoy subtree must not be materialized");

		// [09:00,11:00) at the 100/hr root override (not the 60/hr plain user rate) = 200: proves the
		// override above leafId's own requested subtree was still found.
		var sut = new CostQueries(port);
		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });
		result.ExactCost.Should().Be(new(200m));
	}

	/// <summary>
	///     2026-07-25 scalability-follow-up plan §2.5: a node rate override only affects
	///     <see cref="Domain.Rates.RateResolver" /> when its node is the session's own node or one of its
	///     ancestors -- an override on an unrelated node (here, a sibling of <c>leafId</c>'s own branch)
	///     can never be consulted, so it must neither change the total nor be materialized into
	///     <see cref="WorkerCostInputs.NodeOverrides" />.
	/// </summary>
	[Fact]
	public async Task Unrelated_node_overrides_outside_the_final_node_set_do_not_affect_the_total_or_materialize()
	{
		var (rootId, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		for (var index = 0; index < 20; ++index) {
			var decoy = await jobNodePort.AddChildAsync(new() {
				Context = ContextFor(administratorId),
				ParentId = rootId,
				Description = $"Unrelated override decoy {index}",
				OwnerUserId = administratorId,
				Priority = Priority.Medium,
			});
			await AddNodeRateOverrideAsync(administratorId, workerId, decoy.Id, new(999m));
		}

		var port = CreateCostQueryPort(database.ConnectionString);
		var inputs = await port.GetCostInputsAsync(leafId, At(24), 10_000);
		var worker = inputs.Workers.Should().ContainSingle().Subject;
		worker.NodeOverrides.Should().BeEmpty("none of the seeded overrides are on leafId or one of its ancestors");

		var sut = new CostQueries(port);
		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });
		result.ExactCost.Should().Be(new(120m));
	}

	/// <summary>
	///     2026-07-25 scalability-follow-up plan §2.5: a schedule version's own civil-date window,
	///     resolved in its own IANA zone, must exclude it from expansion once it no longer overlaps the
	///     cost window -- a decade of superseded, non-overlapping versions with a deliberately different
	///     weekly pattern (Monday 01:00-02:00, versus the active version's Thursday 09:00-17:00) proves
	///     none of that history leaks into the [09:00,11:00) Thursday session this test actually costs.
	/// </summary>
	[Fact]
	public async Task Obsolete_schedule_versions_outside_the_cost_window_do_not_affect_the_total()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		var schedulePort = CreateSchedulePort(database.ConnectionString);
		for (var year = 2016; year < 2026; ++year) {
			_ = await schedulePort.AddScheduleVersionAsync(new() {
				Context = ContextFor(administratorId),
				UserId = workerId,
				Schedule = new(
					DateTimeZoneProviders.Tzdb["Europe/London"],
					new(year, 1, 1),
					new(year + 1, 1, 1),
					[new(IsoDayOfWeek.Monday, new(1, 0), new(2, 0))]),
			});
		}

		_ = await schedulePort.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"],
				new(2026, 1, 1),
				null,
				[new(IsoDayOfWeek.Thursday, new(9, 0), new(17, 0))]),
		});
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		result.ExactCost.Should().Be(new(120m));
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	internal abstract IScheduleCommandPort CreateSchedulePort(string connectionString);

	internal abstract IRateCommandPort CreateRatePort(string connectionString);

	internal abstract IWorkSessionCommandPort CreateSessionPort(string connectionString);

	internal abstract ICostQueryPort CreateCostQueryPort(string connectionString);

	internal abstract ICostQueryPort CreateCostQueryPortWithInterceptors(
		string connectionString, IReadOnlyList<IInterceptor> interceptors);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private async Task GiveWorkerFullDayWorkingTimeAsync(AppUserId administratorId, AppUserId workerId)
	{
		var schedulePort = CreateSchedulePort(database.ConnectionString);
		_ = await schedulePort.AddScheduleExceptionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Entry = new(ScheduleExceptionEffect.AddWorkingTime, new(At(0), At(24)), null),
			Reason = "Full working day for cost-query contract tests",
		});
	}

	private async Task CorruptStoredScheduleZoneIdAsync(AppUserId workerId, string ianaTimeZone)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE user_schedule_version SET iana_time_zone = @ianaTimeZone WHERE user_id = @userId;";
		command.AddParameter("@ianaTimeZone", ianaTimeZone);
		command.AddParameter("@userId", workerId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task AddUserCostRateAsync(AppUserId administratorId, AppUserId workerId, HourlyRate rate)
	{
		var ratePort = CreateRatePort(database.ConnectionString);
		_ = await ratePort.AddUserCostRateAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Rate = new(rate, Instant.FromUtc(2000, 1, 1, 0, 0), null),
		});
	}

	private async Task AddNodeRateOverrideAsync(AppUserId administratorId, AppUserId workerId, JobNodeId nodeId, HourlyRate rate)
	{
		var ratePort = CreateRatePort(database.ConnectionString);
		_ = await ratePort.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(nodeId, rate, Instant.FromUtc(2000, 1, 1, 0, 0), null),
		});
	}

	private async Task CreateCorrectedSessionAsync(
		AppUserId administratorId, AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var sessionPort = CreateSessionPort(database.ConnectionString);
		var session = await sessionPort.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		_ = await sessionPort.CorrectSessionAsync(new() {
			Context = ContextFor(administratorId),
			SessionId = session.Id,
			StartedAt = startedAt,
			FinishedAt = finishedAt,
			Reason = "Pin to a deterministic instant for cost-query contract tests",
			Version = session.Version,
		});
	}

	/// <summary>
	///     Seeds a deployed schema, an administrator via the real bootstrap port (which
	///     itself grants <see cref="EmployeeRole.Administrator" />, satisfying every policy this
	///     slice's dependent ports check), one <see cref="EmployeeRole.Worker" /> employee, and a tree shaped
	///     like <c>CostQueriesTests</c>' own fixture: root with children [branch, otherLeaf], branch
	///     with child [leaf].
	/// </summary>
	private async Task<(JobNodeId RootId, JobNodeId BranchId, JobNodeId LeafId, JobNodeId OtherLeafId, AppUserId AdministratorId, AppUserId WorkerId)>
		SeedTreeAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		var workerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Grace Hopper", "grace.hopper.cost", EmployeeRole.Worker);

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var branch = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(result.AdministratorId),
			ParentId = result.RootJobNodeId,
			Description = "Branch",
			OwnerUserId = result.AdministratorId,
			Priority = Priority.Medium,
		});
		var leaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(result.AdministratorId),
			ParentId = branch.Id,
			Description = "Leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(result.AdministratorId), JobNodeId = leaf.Id });
		var otherLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(result.AdministratorId),
			ParentId = result.RootJobNodeId,
			Description = "Other leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(result.AdministratorId), JobNodeId = otherLeaf.Id });

		return (result.RootJobNodeId, branch.Id, leaf.Id, otherLeaf.Id, result.AdministratorId, workerId);
	}








}
