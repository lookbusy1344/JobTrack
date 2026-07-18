namespace JobTrack.Web;

using System.Collections.Concurrent;

public sealed class LoginAttemptRateLimiter
{
	private const int DefaultBackstopPermitMultiplier = 20;
	private const int DefaultMaxPartitionCount = 4096;
	private readonly int backstopPermitLimit;
	private readonly ConcurrentDictionary<string, WindowState> backstopWindows = new(StringComparer.Ordinal);
	private readonly int maxPartitionCount;

	private readonly ConcurrentDictionary<string, WindowState> partitionWindows = new(StringComparer.Ordinal);
	private readonly int permitLimit;
	private readonly TimeProvider timeProvider;
	private readonly TimeSpan window;

	public LoginAttemptRateLimiter(
		int permitLimit,
		TimeSpan window,
		int? backstopPermitLimit = null,
		int maxPartitionCount = DefaultMaxPartitionCount,
		TimeProvider? timeProvider = null)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartitionCount);

		var resolvedBackstopPermitLimit = backstopPermitLimit ?? checked(permitLimit * DefaultBackstopPermitMultiplier);
		ArgumentOutOfRangeException.ThrowIfLessThan(resolvedBackstopPermitLimit, permitLimit);

		this.permitLimit = permitLimit;
		this.backstopPermitLimit = resolvedBackstopPermitLimit;
		this.maxPartitionCount = maxPartitionCount;
		this.window = window;
		this.timeProvider = timeProvider ?? TimeProvider.System;
	}

	public bool TryAcquire(string partitionKey, string backstopKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(backstopKey);

		var now = timeProvider.GetUtcNow();
		PruneExpiredPartitions(partitionWindows, now);
		PruneExpiredPartitions(backstopWindows, now);

		if (WouldExceedPartitionLimit(partitionWindows, partitionKey) || WouldExceedPartitionLimit(backstopWindows, backstopKey)) {
			return false;
		}

		var backstopState = backstopWindows.GetOrAdd(backstopKey, static _ => new());
		var partitionState = partitionWindows.GetOrAdd(partitionKey, static _ => new());
		return TryAcquire(backstopState, backstopPermitLimit, partitionState, permitLimit, now);
	}

	private bool TryAcquire(WindowState firstState, int firstLimit, WindowState secondState, int secondLimit, DateTimeOffset now)
	{
		lock (firstState.Gate) {
			lock (secondState.Gate) {
				ResetIfExpired(firstState, now);
				ResetIfExpired(secondState, now);
				if (firstState.PermitsUsed >= firstLimit || secondState.PermitsUsed >= secondLimit) {
					return false;
				}

				firstState.PermitsUsed++;
				secondState.PermitsUsed++;
				return true;
			}
		}
	}

	private bool WouldExceedPartitionLimit(ConcurrentDictionary<string, WindowState> states, string key) =>
		!states.ContainsKey(key) && states.Count >= maxPartitionCount;

	private void PruneExpiredPartitions(ConcurrentDictionary<string, WindowState> states, DateTimeOffset now)
	{
		foreach (var (key, state) in states) {
			lock (state.Gate) {
				if (IsExpired(state, now)) {
					_ = states.TryRemove(key, out _);
				}
			}
		}
	}

	private void ResetIfExpired(WindowState state, DateTimeOffset now)
	{
		if (IsExpired(state, now)) {
			state.WindowStartedAt = now;
			state.PermitsUsed = 0;
		}
	}

	private bool IsExpired(WindowState state, DateTimeOffset now) => now - state.WindowStartedAt >= window;

	private sealed class WindowState
	{
		public object Gate { get; } = new();

		public DateTimeOffset WindowStartedAt { get; set; } = DateTimeOffset.UnixEpoch;

		public int PermitsUsed { get; set; }
	}
}
