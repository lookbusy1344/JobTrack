namespace JobTrack.Identity;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
///     <see cref="ILoginAttemptRateLimiter" /> over the shared <c>rate_limit_try_consume</c>
///     PostgreSQL function (ADR 0066 Stage 5) -- the <c>MultiInstance</c> counterpart to
///     <c>JobTrack.Web.LoginAttemptRateLimiter</c>'s in-process counters, so the configured permit
///     limit is exact across every host rather than multiplying by instance count. Fails closed
///     (plan §2.4): a counter-store failure reports <see cref="RateLimitOutcome.StoreUnavailable" />,
///     never falling back to an in-process counter or silently admitting the request. Never persists
///     the raw partition/backstop key -- only its SHA-256 digest, alongside the fixed <c>"login"</c>
///     purpose discriminator.
/// </summary>
public sealed class PostgreSqlLoginAttemptRateLimiter(
	PostgreSqlJobTrackIdentityDbContext context,
	TimeProvider timeProvider,
	int permitLimit,
	int backstopPermitLimit,
	TimeSpan window,
	int maxPartitionCount,
	RateLimitMetrics metrics) : ILoginAttemptRateLimiter
{
	private const string Purpose = "login";

	public async ValueTask<RateLimitOutcome> TryAcquireAsync(string partitionKey, string backstopKey, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(backstopKey);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartitionCount);

		using var acquisition = metrics.MeasureAcquisition(Purpose);
		try {
			var result = await context
							   .RateLimitTryConsume(
								   Purpose,
								   Digest(partitionKey),
								   Digest(backstopKey),
								   timeProvider.GetUtcNow(),
								   (int)window.TotalSeconds,
								   permitLimit,
								   backstopPermitLimit,
								   maxPartitionCount)
							   .SingleAsync(cancellationToken)
							   .ConfigureAwait(false);

			metrics.RecordRowsPruned(Purpose, result.OutRowsPruned);
			var outcome = result.OutAllowed ? RateLimitOutcome.Allowed : RateLimitOutcome.Denied;
			metrics.RecordOutcome(Purpose, outcome);
			return outcome;
		}
		catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException) {
			metrics.RecordOutcome(Purpose, RateLimitOutcome.StoreUnavailable);
			return RateLimitOutcome.StoreUnavailable;
		}
	}

	private static byte[] Digest(string rawKey) => SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
}
