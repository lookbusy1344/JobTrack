namespace JobTrack.Web;

using System.Threading.RateLimiting;
using Identity;

/// <summary>
///     Wraps the same <see cref="System.Threading.RateLimiting.PartitionedRateLimiter{TResource}" />/
///     <see cref="RateLimitPartition" /> fixed-window primitive the framework's own
///     <c>AddRateLimiter</c> middleware used before ADR 0066 Stage 5, so <c>SingleInstance</c>
///     behaviour is unchanged -- only the attachment point moved to <c>Program.cs</c>'s own
///     rate-limit middleware, shared with <see cref="Identity.PostgreSqlApiRateLimitStore" />
///     (<c>MultiInstance</c>) via <see cref="IApiRateLimitStore" />.
/// </summary>
internal sealed class InProcessApiRateLimitStore : IApiRateLimitStore, IDisposable
{
	private readonly PartitionedRateLimiter<string> limiter;

	internal InProcessApiRateLimitStore(int permitLimit, TimeSpan window) =>
		limiter = PartitionedRateLimiter.Create<string, string>(key =>
			RateLimitPartition.GetFixedWindowLimiter(key, _ => new() {
				PermitLimit = permitLimit,
				Window = window,
				QueueLimit = 0,
			}));

	public async ValueTask<RateLimitOutcome> TryAcquireAsync(string partitionKey, CancellationToken cancellationToken)
	{
		using var lease = await limiter.AcquireAsync(partitionKey, 1, cancellationToken);
		return lease.IsAcquired ? RateLimitOutcome.Allowed : RateLimitOutcome.Denied;
	}

	public void Dispose() => limiter.Dispose();
}
