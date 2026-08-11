namespace JobTrack.Web;

using System.Collections.Concurrent;
using Identity;

/// <summary>
///     In-process store: under 2+ instances the configured limit effectively multiplies, since each
///     instance counts attempts independently -- see
///     docs/operations/production-deployment.md's multi-instance in-process-state table.
/// </summary>
/// <remarks>
///     Backed by bounded, atomic FIFO caches (security review remediation §2.8): a full table evicts
///     an existing state before admitting a new one instead of hard-rejecting every unseen partition.
///     Unlike <c>MemoryCache</c>'s size-limit rejection path, an admitted key is always retained with
///     its consumed permit; no request can proceed against an uncached zero-count fallback state.
/// </remarks>
public sealed class LoginAttemptRateLimiter : IDisposable, ILoginAttemptRateLimiter
{
	private const int DefaultBackstopPermitMultiplier = 20;
	private const int DefaultMaxPartitionCount = 4096;
	private readonly int backstopPermitLimit;
	private readonly BoundedWindowCache backstopWindows;
	private readonly BoundedWindowCache partitionWindows;
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
		this.window = window;
		this.timeProvider = timeProvider ?? TimeProvider.System;

		partitionWindows = new(maxPartitionCount);
		backstopWindows = new(maxPartitionCount);
	}

	public void Dispose()
	{
		partitionWindows.Clear();
		backstopWindows.Clear();
	}

	/// <summary>Never returns <see cref="RateLimitOutcome.StoreUnavailable" /> -- an in-process cache cannot itself be unavailable.</summary>
	ValueTask<RateLimitOutcome> ILoginAttemptRateLimiter.
		TryAcquireAsync(string partitionKey, string backstopKey, CancellationToken cancellationToken) =>
		ValueTask.FromResult(TryAcquire(partitionKey, backstopKey) ? RateLimitOutcome.Allowed : RateLimitOutcome.Denied);

	public bool TryAcquire(string partitionKey, string backstopKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(backstopKey);

		var now = timeProvider.GetUtcNow();
		var backstopState = GetOrCreateWindow(backstopWindows, backstopKey);
		var partitionState = GetOrCreateWindow(partitionWindows, partitionKey);
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

				++firstState.PermitsUsed;
				++secondState.PermitsUsed;
				return true;
			}
		}
	}

	/// <summary>
	///     Atomically returns one state for a key. Capacity pressure evicts the oldest retained key;
	///     it never returns a newly created state merely because the cache refused to store it.
	/// </summary>
	private static WindowState GetOrCreateWindow(BoundedWindowCache cache, string key) => cache.GetOrAdd(key);

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

	private sealed class BoundedWindowCache(int capacity)
	{
		private readonly ConcurrentQueue<(string Key, WindowState State)> insertionOrder = new();
		private readonly ConcurrentDictionary<string, WindowState> windows = new(StringComparer.Ordinal);

		public WindowState GetOrAdd(string key)
		{
			var state = windows.GetOrAdd(
				key,
				static (newKey, queue) => {
					var created = new WindowState();
					queue.Enqueue((newKey, created));
					return created;
				},
				insertionOrder);
			TrimToCapacity();
			return state;
		}

		public void Clear()
		{
			windows.Clear();
			insertionOrder.Clear();
		}

		private void TrimToCapacity()
		{
			while (windows.Count > capacity && insertionOrder.TryDequeue(out var candidate)) {
				if (windows.TryGetValue(candidate.Key, out var current) && ReferenceEquals(current, candidate.State)) {
					_ = windows.TryRemove(candidate.Key, out _);
				}
			}
		}
	}
}
