namespace JobTrack.Web;

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
	private readonly Lock gate = new();
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
		lock (gate) {
			partitionWindows.Clear();
			backstopWindows.Clear();
		}
	}

	/// <summary>Never returns <see cref="RateLimitOutcome.StoreUnavailable" /> -- an in-process cache cannot itself be unavailable.</summary>
	ValueTask<RateLimitOutcome> ILoginAttemptRateLimiter.
		TryAcquireAsync(string partitionKey, string backstopKey, CancellationToken cancellationToken) =>
		ValueTask.FromResult(TryAcquire(partitionKey, backstopKey) ? RateLimitOutcome.Allowed : RateLimitOutcome.Denied);

	public bool TryAcquire(string partitionKey, string backstopKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(backstopKey);

		lock (gate) {
			var now = timeProvider.GetUtcNow();
			var backstopExists = backstopWindows.TryGet(backstopKey, out var backstopState);
			backstopState ??= new();
			ResetIfExpired(backstopState, now);
			if (backstopState.PermitsUsed >= backstopPermitLimit) {
				return false;
			}

			var partitionExists = partitionWindows.TryGet(partitionKey, out var partitionState);
			partitionState ??= new();
			ResetIfExpired(partitionState, now);
			if (partitionState.PermitsUsed >= permitLimit) {
				return false;
			}

			if (!backstopExists) {
				backstopWindows.Add(backstopKey, backstopState);
			}

			if (!partitionExists) {
				partitionWindows.Add(partitionKey, partitionState);
			}

			++backstopState.PermitsUsed;
			++partitionState.PermitsUsed;
			return true;
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
		public DateTimeOffset WindowStartedAt { get; set; } = DateTimeOffset.UnixEpoch;

		public int PermitsUsed { get; set; }
	}

	private sealed class BoundedWindowCache(int capacity)
	{
		private readonly Queue<string> insertionOrder = new();
		private readonly Dictionary<string, WindowState> windows = new(StringComparer.Ordinal);

		public void Add(string key, WindowState state)
		{
			while (windows.Count >= capacity) {
				_ = windows.Remove(insertionOrder.Dequeue());
			}

			windows.Add(key, state);
			insertionOrder.Enqueue(key);
		}

		public bool TryGet(string key, out WindowState? state) => windows.TryGetValue(key, out state);

		public void Clear()
		{
			windows.Clear();
			insertionOrder.Clear();
		}
	}
}
