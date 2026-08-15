namespace JobTrack.Web.IntegrationTests;

using AwesomeAssertions;

public sealed class ReadinessProbeGateTests
{
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

	[Fact]
	public async Task Concurrent_readiness_checks_share_one_dependency_probe()
	{
		var timeProvider = new ManualTimeProvider();
		using var gate = new ReadinessProbeGate(timeProvider, CacheLifetime);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var callCount = 0;

		async Task<bool> ProbeAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref callCount);
			await release.Task.WaitAsync(cancellationToken);
			return true;
		}

		var checks = Enumerable.Range(0, 20)
							   .Select(_ => gate.CheckAsync(ProbeAsync, CancellationToken.None))
							   .ToArray();
		await Task.Yield();

		callCount.Should().Be(1);
		release.SetResult();
		(await Task.WhenAll(checks)).Should().AllBeEquivalentTo(true);
	}

	[Fact]
	public async Task Readiness_result_is_reused_only_within_the_short_cache_lifetime()
	{
		var timeProvider = new ManualTimeProvider();
		using var gate = new ReadinessProbeGate(timeProvider, CacheLifetime);
		var callCount = 0;

		Task<bool> ProbeAsync(CancellationToken _)
		{
			++callCount;
			return Task.FromResult(true);
		}

		(await gate.CheckAsync(ProbeAsync, CancellationToken.None)).Should().BeTrue();
		timeProvider.Advance(CacheLifetime - TimeSpan.FromMilliseconds(1));
		(await gate.CheckAsync(ProbeAsync, CancellationToken.None)).Should().BeTrue();
		callCount.Should().Be(1);

		timeProvider.Advance(TimeSpan.FromMilliseconds(1));
		(await gate.CheckAsync(ProbeAsync, CancellationToken.None)).Should().BeTrue();
		callCount.Should().Be(2);
	}

	private sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

		public override DateTimeOffset GetUtcNow() => utcNow;

		public void Advance(TimeSpan duration) => utcNow += duration;
	}
}
