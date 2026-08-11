namespace JobTrack.Identity;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
///     Shared PostgreSQL primitive, no backstop partition -- see <see cref="PostgreSqlLoginAttemptRateLimiter" /> for the fail-closed/digest
///     rationale.
/// </summary>
public sealed class PostgreSqlApiRateLimitStore(
	PostgreSqlJobTrackIdentityDbContext context,
	TimeProvider timeProvider,
	int permitLimit,
	TimeSpan window,
	int maxPartitionCount,
	RateLimitMetrics metrics) : IApiRateLimitStore
{
	private const string Purpose = "api";

	public async ValueTask<RateLimitOutcome> TryAcquireAsync(string partitionKey, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartitionCount);

		using var acquisition = metrics.MeasureAcquisition(Purpose);
		try {
			var result = await context
				.RateLimitTryConsume(
					Purpose,
					Digest(partitionKey),
					null,
					timeProvider.GetUtcNow(),
					(int)window.TotalSeconds,
					permitLimit,
					0,
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
