namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;

public sealed class LoginAttemptRateLimiterTests
{
	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	[Fact]
	public void Different_partitions_have_independent_budgets()
	{
		var clock = new ManualTimeProvider();
		var limiter = new LoginAttemptRateLimiter(
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
		var limiter = new LoginAttemptRateLimiter(
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
		var limiter = new LoginAttemptRateLimiter(
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
		var limiter = new LoginAttemptRateLimiter(
			1,
			Window,
			1,
			10,
			clock);

		limiter.TryAcquire("password:127.0.0.1:ADA", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("two-factor:127.0.0.1:ADA", "two-factor:127.0.0.1").Should().BeTrue();
	}

	[Fact]
	public void Expired_partitions_are_pruned_before_accepting_new_keys()
	{
		var clock = new ManualTimeProvider();
		var limiter = new LoginAttemptRateLimiter(
			1,
			Window,
			10,
			1,
			clock);

		limiter.TryAcquire("password:127.0.0.1:ONE", "password:127.0.0.1").Should().BeTrue();
		limiter.TryAcquire("password:127.0.0.1:TWO", "password:127.0.0.1").Should().BeFalse();

		clock.Advance(Window + TimeSpan.FromSeconds(1));

		limiter.TryAcquire("password:127.0.0.1:TWO", "password:127.0.0.1").Should().BeTrue();
	}

	private sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset current = DateTimeOffset.UnixEpoch;

		public override DateTimeOffset GetUtcNow() => current;

		public void Advance(TimeSpan value) => current += value;
	}
}
