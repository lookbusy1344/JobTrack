namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
///     ADR 0066 Stage 5 item 6 (docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.4):
///     "a counter-store failure ... never silently falls back to a local counter or allows the
///     request." Both PostgreSQL-backed limiters must report
///     <see cref="RateLimitOutcome.StoreUnavailable" />, not <see cref="RateLimitOutcome.Allowed" />,
///     when their connection cannot be reached -- proven here against an unroutable loopback port
///     with an aggressive timeout, rather than a live server, so the test fails fast and needs no
///     real outage to reproduce.
/// </summary>
public sealed class PostgreSqlRateLimitStoreFailureTests
{
	private const int MaxPartitionCount = 4096;

	// Port 1 on loopback is never listening; Timeout=1 keeps Npgsql's connection attempt from
	// waiting out its default 15s budget.
	private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=jobtrack_unreachable;Timeout=1";

	[Fact]
	public async Task The_login_limiter_fails_closed_when_the_store_is_unreachable()
	{
		await using var context = CreateUnreachableContext();
		using var metrics = CreateMetrics();
		var limiter = new PostgreSqlLoginAttemptRateLimiter(
			context, TimeProvider.System, 5, 100, TimeSpan.FromSeconds(60), MaxPartitionCount, metrics);

		var outcome = await limiter.TryAcquireAsync("partition", "backstop", CancellationToken.None);

		outcome.Should().Be(RateLimitOutcome.StoreUnavailable);
	}

	[Fact]
	public async Task The_api_limiter_fails_closed_when_the_store_is_unreachable()
	{
		await using var context = CreateUnreachableContext();
		using var metrics = CreateMetrics();
		var store = new PostgreSqlApiRateLimitStore(
			context, TimeProvider.System, 5, TimeSpan.FromSeconds(60), MaxPartitionCount, metrics);

		var outcome = await store.TryAcquireAsync("partition", CancellationToken.None);

		outcome.Should().Be(RateLimitOutcome.StoreUnavailable);
	}

	private static PostgreSqlJobTrackIdentityDbContext CreateUnreachableContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackIdentityDbContext>().UseNpgsql(UnreachableConnectionString).Options;
		return new(options);
	}

	private static RateLimitMetrics CreateMetrics()
	{
		var services = new ServiceCollection();
		_ = services.AddDbContext<PostgreSqlJobTrackIdentityDbContext>(options => options.UseNpgsql(UnreachableConnectionString));
		return new(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
	}
}
