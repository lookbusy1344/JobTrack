namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;

public sealed class LoginAttemptRateLimiterTests
{
	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	[Fact]
	public void Different_partitions_have_independent_budgets()
	{
		var clock = new ManualTimeProvider();
		using var limiter = new LoginAttemptRateLimiter(
			1,
			Window,
			10,
			10,
			clock);

		limiter.TryAcquire("password:127.0.0.1:ADA", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("password:127.0.0.1:ADA", "password:127.0.0.1").Should().BeFalse();
		limiter.TryAcquire("password:127.0.0.1:GRACE", "password:127.0.0.1").Should().BeTrue();
	}

	[Fact]
	public void Backstop_limits_partition_rotation_within_one_origin()
	{
		var clock = new ManualTimeProvider();
		using var limiter = new LoginAttemptRateLimiter(
			1,
			Window,
			2,
			10,
			clock);

		limiter.TryAcquire("password:127.0.0.1:ONE", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("password:127.0.0.1:TWO", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("password:127.0.0.1:THREE", "password:127.0.0.1").Should().BeFalse();
	}

	[Fact]
	public void Backstop_does_not_cross_remote_origins()
	{
		var clock = new ManualTimeProvider();
		using var limiter = new LoginAttemptRateLimiter(
			1,
			Window,
			1,
			10,
			clock);

		limiter.TryAcquire("password:127.0.0.1:ONE", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("password:127.0.0.1:TWO", "password:127.0.0.1").Should().BeFalse();
		limiter.TryAcquire("password:127.0.0.2:TWO", "password:127.0.0.2").Should().BeTrue();
	}

	[Fact]
	public void Password_and_two_factor_backstops_are_independent()
	{
		var clock = new ManualTimeProvider();
		using var limiter = new LoginAttemptRateLimiter(
			1,
			Window,
			1,
			10,
			clock);

		limiter.TryAcquire("password:127.0.0.1:ADA", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("two-factor:127.0.0.1:ADA", "two-factor:127.0.0.1").Should().BeTrue();
	}

	[Fact]
	public void An_expired_window_resets_the_permit_count_for_the_same_key()
	{
		var clock = new ManualTimeProvider();
		using var limiter = new LoginAttemptRateLimiter(1, Window, 10, 10, clock);

		limiter.TryAcquire("password:127.0.0.1:ONE", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("password:127.0.0.1:ONE", "password:127.0.0.1").Should().BeFalse();

		clock.Advance(Window + TimeSpan.FromSeconds(1));

		limiter.TryAcquire("password:127.0.0.1:ONE", "password:127.0.0.1").Should().BeTrue();
	}

	/// <summary>
	///     Security review remediation §2.8: the prior <c>ConcurrentDictionary</c>-backed limiter
	///     hard-rejected every previously unseen partition once the shared table reached
	///     <c>maxPartitionCount</c>, turning the memory bound into an authentication-availability
	///     switch an attacker could trip by rotating usernames/addresses. The <see cref="MemoryCache" />-
	///     backed replacement evicts existing entries under size pressure instead, so a brand-new
	///     partition is still admitted once the table is full rather than being permanently denied.
	/// </summary>
	[Fact]
	public void A_full_partition_table_admits_a_new_key_instead_of_permanently_rejecting_it()
	{
		var clock = new ManualTimeProvider();
		// A high backstop limit isolates this test to partition-table capacity: with 20 distinct
		// per-attempt origins sharing one backstop key, a low backstop limit would otherwise become
		// the thing that blocks the final acquire, not the partition table under test.
		using var limiter = new LoginAttemptRateLimiter(1, Window, 100, 4, clock);

		for (var i = 0; i < 20; ++i) {
			_ = limiter.TryAcquire($"password:127.0.0.1:ATTACKER-{i}", "password:127.0.0.1");
		}

		limiter.TryAcquire("password:127.0.0.1:LEGITIMATE-USER", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("password:127.0.0.1:LEGITIMATE-USER", "password:127.0.0.1").Should().BeFalse(
			"a key admitted under capacity pressure must retain its consumed permit");
	}

	/// <summary>Same property as above, isolated to the backstop table (keyed by origin, not username).</summary>
	[Fact]
	public void A_full_backstop_table_admits_a_new_origin_instead_of_permanently_rejecting_it()
	{
		var clock = new ManualTimeProvider();
		using var limiter = new LoginAttemptRateLimiter(1, Window, 1, 4, clock);

		for (var i = 0; i < 20; ++i) {
			_ = limiter.TryAcquire($"password:10.0.0.{i}:attacker", $"password:10.0.0.{i}");
		}

		limiter.TryAcquire("password:203.0.113.1:legitimate-user", "password:203.0.113.1").Should().BeTrue();
		limiter.TryAcquire("password:203.0.113.1:legitimate-user", "password:203.0.113.1").Should().BeFalse(
			"a backstop admitted under capacity pressure must retain its consumed permit");
	}

	private sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset current = DateTimeOffset.UnixEpoch;

		public override DateTimeOffset GetUtcNow() => current;

		public void Advance(TimeSpan value) => current += value;
	}
}
