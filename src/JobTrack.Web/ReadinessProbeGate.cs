namespace JobTrack.Web;

/// <summary>
///     Coalesces concurrent dependency readiness checks and briefly reuses their result, so an
///     anonymous health-check burst cannot translate one-for-one into database connection attempts.
/// </summary>
internal sealed class ReadinessProbeGate(TimeProvider timeProvider, TimeSpan cacheLifetime) : IDisposable
{
	private readonly SemaphoreSlim gate = new(1, 1);
	private DateTimeOffset? lastCheckedAt;
	private bool lastResult;

	public async Task<bool> CheckAsync(Func<CancellationToken, Task<bool>> probeAsync, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(probeAsync);

		await gate.WaitAsync(cancellationToken);
		try {
			var now = timeProvider.GetUtcNow();
			if (lastCheckedAt is DateTimeOffset checkedAt && now - checkedAt < cacheLifetime) {
				return lastResult;
			}

			lastResult = await probeAsync(cancellationToken);
			lastCheckedAt = timeProvider.GetUtcNow();
			return lastResult;
		}
		finally {
			_ = gate.Release();
		}
	}

	public void Dispose() => gate.Dispose();
}
