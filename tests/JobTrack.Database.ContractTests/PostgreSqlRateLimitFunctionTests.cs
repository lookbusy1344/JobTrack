namespace JobTrack.Database.ContractTests;

using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

/// <summary>
///     TC-DB-RATELIMIT-001: contract/concurrency coverage for the shared
///     <c>rate_limit_try_consume</c> PostgreSQL function (ADR 0066 Stage 5,
///     docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.4) -- the primitive both the
///     login and external API limiters call through once multi-instance is enabled. Each test opens
///     its own <see cref="PostgreSqlJobTrackIdentityDbContext" /> (an independent connection, per
///     plan §5's "concurrent callers across independent connections"), matching how two separate web
///     hosts would each hold their own pool.
/// </summary>
public sealed class PostgreSqlRateLimitFunctionTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string FirstOrigin = "192.0.2.10";
	private const string LoginPurpose = "login";
	private const int MaxPartitionCount = 3;
	private const string SecondOrigin = "198.51.100.20";

	private readonly PostgreSqlDatabaseFixture database = new();

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();
	}

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Consuming_up_to_the_permit_limit_succeeds_and_the_next_call_is_denied()
	{
		var digest = Digest("alice");
		var now = DateTimeOffset.UtcNow;

		await using var context = CreateContext();
		var first = await TryConsumeAsync(context, digest, null, now, 60, 2, 0);
		var second = await TryConsumeAsync(context, digest, null, now, 60, 2, 0);
		var third = await TryConsumeAsync(context, digest, null, now, 60, 2, 0);

		first.Should().BeTrue();
		second.Should().BeTrue();
		third.Should().BeFalse("the permit limit is 2, so a third call in the same window must be denied");
	}

	[Fact]
	public async Task A_call_naming_no_backstop_never_touches_a_different_partitions_count()
	{
		var digestA = Digest("partition-a");
		var digestB = Digest("partition-b");
		var now = DateTimeOffset.UtcNow;

		await using var context = CreateContext();
		_ = await TryConsumeAsync(context, digestA, null, now, 60, 1, 0);
		var stillAvailable = await TryConsumeAsync(context, digestB, null, now, 60, 1, 0);

		stillAvailable.Should().BeTrue("consuming one partition must not affect an unrelated partition's own count");
	}

	[Fact]
	public async Task A_backstop_partition_at_its_own_limit_denies_the_call_even_when_the_primary_partition_has_room()
	{
		var primaryDigest = Digest("primary-1");
		var backstopDigest = Digest("shared-backstop");
		var now = DateTimeOffset.UtcNow;

		await using var context = CreateContext();
		// Exhaust the backstop through a different primary partition sharing it, mirroring the login
		// limiter's per-username-plus-IP primary against a per-IP backstop.
		_ = await TryConsumeAsync(context, Digest("primary-2"), backstopDigest, now, 60, 10, 1);

		var denied = await TryConsumeAsync(context, primaryDigest, backstopDigest, now, 60, 10, 1);

		denied.Should().BeFalse();
	}

	[Fact]
	public async Task Rotating_primary_keys_cannot_evict_or_reset_their_exhausted_backstop()
	{
		const int BackstopPermitLimit = 2;
		const int PrimaryPermitLimit = 10;
		const int WindowSeconds = 60;
		var backstopDigest = Digest("password:192.0.2.10");
		var deniedPrimaryDigest = Digest("password:192.0.2.10:THREE");
		var now = DateTimeOffset.UtcNow;

		await using var context = CreateContext();
		var first = await TryConsumeBoundedAsync(
			context, Digest("password:192.0.2.10:ONE"), backstopDigest, now,
			WindowSeconds, PrimaryPermitLimit, BackstopPermitLimit, MaxPartitionCount);
		var second = await TryConsumeBoundedAsync(
			context, Digest("password:192.0.2.10:TWO"), backstopDigest, now,
			WindowSeconds, PrimaryPermitLimit, BackstopPermitLimit, MaxPartitionCount);
		var denied = await TryConsumeBoundedAsync(
			context, deniedPrimaryDigest, backstopDigest, now,
			WindowSeconds, PrimaryPermitLimit, BackstopPermitLimit, MaxPartitionCount);

		first.OutAllowed.Should().BeTrue();
		second.OutAllowed.Should().BeTrue();
		denied.OutAllowed.Should().BeFalse();
		var deniedPrimaryCount = await CountPartitionAsync(context, 0, deniedPrimaryDigest);
		deniedPrimaryCount.Should().Be(0, "backstop denial precedes primary-pool admission");
	}

	[Theory]
	[InlineData("passkey-options")]
	[InlineData("passkey-assertion")]
	public async Task Passkey_origin_backstops_are_independent(string purpose)
	{
		const int PermitLimit = 1;
		const int WindowSeconds = 60;
		var now = DateTimeOffset.UtcNow;

		await using var context = CreateContext();
		var first = await TryConsumeAsync(
			context,
			Digest($"{purpose}:request:{FirstOrigin}"),
			Digest($"{purpose}:origin:{FirstOrigin}"),
			now,
			WindowSeconds,
			PermitLimit,
			PermitLimit);
		var exhausted = await TryConsumeAsync(
			context,
			Digest($"{purpose}:request:{FirstOrigin}"),
			Digest($"{purpose}:origin:{FirstOrigin}"),
			now,
			WindowSeconds,
			PermitLimit,
			PermitLimit);
		var unrelated = await TryConsumeAsync(
			context,
			Digest($"{purpose}:request:{SecondOrigin}"),
			Digest($"{purpose}:origin:{SecondOrigin}"),
			now,
			WindowSeconds,
			PermitLimit,
			PermitLimit);

		first.Should().BeTrue();
		exhausted.Should().BeFalse();
		unrelated.Should().BeTrue();
	}

	[Fact]
	public async Task Denial_from_an_exhausted_backstop_does_not_increment_the_primary_partition()
	{
		var primaryDigest = Digest("primary-guarded");
		var backstopDigest = Digest("backstop-exhausted");
		var now = DateTimeOffset.UtcNow;

		await using var context = CreateContext();
		_ = await TryConsumeAsync(context, Digest("other-primary"), backstopDigest, now, 60, 10, 1);
		var denied = await TryConsumeAsync(context, primaryDigest, backstopDigest, now, 60, 10, 1);

		denied.Should().BeFalse();

		// The primary partition must still show its full, untouched limit -- a partial decision
		// (backstop denied but primary silently incremented anyway) would let it now admit one fewer
		// caller than its own limit allows, for no reason visible from its own count.
		var stillHasFullCapacity = true;
		for (var i = 0; i < 10; ++i) {
			stillHasFullCapacity &= await TryConsumeAsync(context, primaryDigest, null, now, 60, 10, 0);
		}

		stillHasFullCapacity.Should().BeTrue("a denied backstop must never have consumed a permit from the primary partition");
	}

	[Fact]
	public async Task Concurrent_callers_across_independent_connections_never_exceed_the_permit_limit()
	{
		const int PermitLimit = 5;
		const int CallerCount = 20;
		var digest = Digest("concurrent");
		var now = DateTimeOffset.UtcNow;

		var results = await Task.WhenAll(Enumerable.Range(0, CallerCount).Select(async _ => {
			await using var context = CreateContext();
			return await TryConsumeAsync(context, digest, null, now, 60, PermitLimit, 0);
		}));

		results.Count(succeeded => succeeded).Should()
			   .Be(PermitLimit, "exactly the configured permit limit must succeed, regardless of concurrent contention");
	}

	[Fact]
	public async Task A_new_window_resets_the_count_independently_of_the_previous_one()
	{
		var digest = Digest("windowed");
		var firstWindow = DateTimeOffset.UtcNow;
		var secondWindow = firstWindow.AddSeconds(61);

		await using var context = CreateContext();
		_ = await TryConsumeAsync(context, digest, null, firstWindow, 60, 1, 0);
		var deniedInFirstWindow = await TryConsumeAsync(context, digest, null, firstWindow, 60, 1, 0);
		var allowedInSecondWindow = await TryConsumeAsync(context, digest, null, secondWindow, 60, 1, 0);

		deniedInFirstWindow.Should().BeFalse();
		allowedInSecondWindow.Should().BeTrue("a new fixed window must not inherit the previous window's consumed permits");
	}

	[Fact]
	public async Task A_call_that_prunes_an_expired_window_reports_how_many_rows_it_removed()
	{
		var digest = Digest("prunable");
		var expiredWindow = DateTimeOffset.UtcNow;
		var nextWindow = expiredWindow.AddSeconds(61);

		await using var context = CreateContext();
		_ = await TryConsumeRawAsync(context, digest, null, expiredWindow, 60, 1, 0);

		var afterExpiry = await TryConsumeRawAsync(context, digest, null, nextWindow, 60, 1, 0);

		afterExpiry.OutAllowed.Should().BeTrue();
		afterExpiry.OutRowsPruned.Should().Be(1, "the first window's now-expired row must be pruned by the call that opens the next window");
	}

	[Fact]
	public async Task Capacity_pressure_admits_a_new_partition_by_evicting_an_existing_partition()
	{
		var now = DateTimeOffset.UtcNow;

		await using var context = CreateContext();
		var outcomes = new List<RateLimitConsumeResult>();
		for (var i = 0; i < MaxPartitionCount + 1; ++i) {
			var result = await TryConsumeBoundedAsync(context, Digest($"partition-{i}"), null, now, 60, 1, 0, MaxPartitionCount);
			outcomes.Add(result);
		}

		outcomes.Should().OnlyContain(result => result.OutAllowed, "capacity is a storage bound, not a denial policy");
		outcomes.Sum(result => result.OutRowsEvicted).Should().Be(1, "the fourth partition replaces one retained primary row");
		var livePartitionCount = await context.Database
											  .SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM rate_limit_window WHERE purpose = {LoginPurpose}")
											  .SingleAsync();
		livePartitionCount.Should().Be(MaxPartitionCount);
	}

	[Fact]
	public async Task A_denied_backstop_cannot_fill_the_table_with_zero_count_primary_partitions()
	{
		var now = DateTimeOffset.UtcNow;
		var backstopDigest = Digest("shared-backstop");

		await using var context = CreateContext();
		_ = await TryConsumeBoundedAsync(context, Digest("first-primary"), backstopDigest, now, 60, 10, 1, MaxPartitionCount);
		var deniedPrimaryDigest = Digest("denied-primary");
		for (var i = 0; i < MaxPartitionCount + 1; ++i) {
			var result = await TryConsumeBoundedAsync(
				context, deniedPrimaryDigest, backstopDigest, now, 60, 10, 1, MaxPartitionCount);
			result.OutAllowed.Should().BeFalse();
		}

		var deniedPrimaryCount = await context.Database
											  .SqlQuery<int>(
												  $"SELECT count(*)::integer AS \"Value\" FROM rate_limit_window WHERE purpose = {LoginPurpose} AND partition_kind = 0 AND partition_digest = {deniedPrimaryDigest}")
											  .SingleAsync();
		deniedPrimaryCount.Should().Be(0, "a denied backstop must be checked before a missing primary is admitted");
	}

	[Fact]
	public async Task Concurrent_unique_partitions_across_independent_connections_cannot_overrun_the_bound()
	{
		const int CallerCount = 12;
		var now = DateTimeOffset.UtcNow;

		var results = await Task.WhenAll(Enumerable.Range(0, CallerCount).Select(async i => {
			await using var context = CreateContext();
			return await TryConsumeBoundedAsync(
				context, Digest($"concurrent-unique-{i}"), null, now, 60, 1, 0, MaxPartitionCount);
		}));

		results.Should().OnlyContain(result => result.OutAllowed, "new callers remain available while the pool evicts under pressure");
		await using var verificationContext = CreateContext();
		var livePartitionCount = await verificationContext.Database
														  .SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM rate_limit_window WHERE purpose = {LoginPurpose}")
														  .SingleAsync();
		livePartitionCount.Should().Be(MaxPartitionCount);
	}

	private static async Task<bool> TryConsumeAsync(
		PostgreSqlJobTrackIdentityDbContext context, byte[] partitionDigest, byte[]? backstopDigest, DateTimeOffset now, int windowSeconds,
		int permitLimit, int backstopPermitLimit)
	{
		var result = await TryConsumeRawAsync(context, partitionDigest, backstopDigest, now, windowSeconds, permitLimit, backstopPermitLimit);
		return result.OutAllowed;
	}

	private static async Task<RateLimitConsumeResult> TryConsumeRawAsync(
		PostgreSqlJobTrackIdentityDbContext context, byte[] partitionDigest, byte[]? backstopDigest, DateTimeOffset now, int windowSeconds,
		int permitLimit, int backstopPermitLimit) =>
		await context.Database
					 .SqlQuery<RateLimitConsumeResult>(
						 $"""
						  SELECT out_allowed AS "OutAllowed", out_rows_pruned AS "OutRowsPruned", out_rows_evicted AS "OutRowsEvicted"
						  FROM rate_limit_try_consume(
						      {LoginPurpose}, {partitionDigest}, {backstopDigest}, {now}, {windowSeconds}, {permitLimit}, {backstopPermitLimit})
						  """)
					 .SingleAsync();

	private static async Task<RateLimitConsumeResult> TryConsumeBoundedAsync(
		PostgreSqlJobTrackIdentityDbContext context, byte[] partitionDigest, byte[]? backstopDigest, DateTimeOffset now, int windowSeconds,
		int permitLimit, int backstopPermitLimit, int maxPartitionCount) =>
		await context.Database
					 .SqlQuery<RateLimitConsumeResult>(
						 $"""
						  SELECT out_allowed AS "OutAllowed", out_rows_pruned AS "OutRowsPruned", out_rows_evicted AS "OutRowsEvicted"
						  FROM rate_limit_try_consume(
						      {LoginPurpose}, {partitionDigest}, {backstopDigest}, {now}, {windowSeconds}, {permitLimit}, {backstopPermitLimit}, {maxPartitionCount})
						  """)
					 .SingleAsync();

	private static byte[] Digest(string rawKey) => SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));

	private static async Task<int> CountPartitionAsync(
		PostgreSqlJobTrackIdentityDbContext context,
		short partitionKind,
		byte[] partitionDigest) =>
		await context.Database
					 .SqlQuery<int>(
						 $"SELECT count(*)::integer AS \"Value\" FROM rate_limit_window WHERE purpose = {LoginPurpose} AND partition_kind = {partitionKind} AND partition_digest = {partitionDigest}")
					 .SingleAsync();

	private PostgreSqlJobTrackIdentityDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackIdentityDbContext>().UseNpgsql(database.ConnectionString).Options;
		return new(options);
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlRolesAndGrantsScriptPath(), CancellationToken.None);
		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlFunctionsScriptPath(), CancellationToken.None);
	}

	private sealed record RateLimitConsumeResult(bool OutAllowed, int OutRowsPruned, int OutRowsEvicted);
}
