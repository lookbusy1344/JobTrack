namespace JobTrack.Identity;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Observability for the shared PostgreSQL rate limiter (ADR 0066 Stage 5 item 6,
///     docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.4): acquisition latency,
///     rejection count, store-failure count, and rows pruned are recorded per call; live partition
///     count is an <see cref="Meter.CreateObservableGauge{T}(string, Func{T}, string, string)" /> pull
///     callback, invoked only when something actually scrapes the meter (a metrics exporter, or
///     <c>dotnet-counters</c>), rather than a background timer -- ADR 0066 Stage 0's inventory found
///     no hosted services in <c>JobTrack.Web</c>, and this does not add one. The gauge needs its own
///     short-lived <see cref="PostgreSqlJobTrackIdentityDbContext" /> scope (this is a singleton;
///     the context is request-scoped), created via <see cref="IServiceScopeFactory" /> only when the
///     callback actually fires. No partition key, digest, username, or IP address is ever recorded on
///     any instrument (plan §2.4: "without logging partition keys").
/// </summary>
public sealed class RateLimitMetrics : IDisposable
{
	private const string MeterName = "JobTrack.RateLimiting";
	private readonly Histogram<double> acquisitionLatencyMilliseconds;

	private readonly Meter meter = new(MeterName);
	private readonly Counter<long> rejections;
	private readonly Counter<long> rowsPruned;
	private readonly IServiceScopeFactory scopeFactory;
	private readonly Counter<long> storeFailures;

	public RateLimitMetrics(IServiceScopeFactory scopeFactory)
	{
		this.scopeFactory = scopeFactory;

		acquisitionLatencyMilliseconds = meter.CreateHistogram<double>(
			"jobtrack.ratelimit.acquisition_latency", "ms", "Time spent deciding whether to admit a rate-limited request.");
		rejections = meter.CreateCounter<long>("jobtrack.ratelimit.rejections",
			description: "Requests denied because their partition's permit limit was exhausted.");
		storeFailures = meter.CreateCounter<long>(
			"jobtrack.ratelimit.store_failures",
			description: "Rate-limit checks that failed closed because the shared counter store was unavailable.");
		rowsPruned = meter.CreateCounter<long>("jobtrack.ratelimit.rows_pruned",
			description: "Expired rate_limit_window rows removed by the consuming call itself.");
		_ = meter.CreateObservableGauge(
			"jobtrack.ratelimit.live_partitions",
			ReadLivePartitionCount,
			description: "Approximate live rate_limit_window row count (pg_class.reltuples -- a catalog estimate, not an exact scan).");
	}

	public void Dispose() => meter.Dispose();

	public IDisposable MeasureAcquisition(string purpose)
	{
		var start = TimeProvider.System.GetTimestamp();
		return new AcquisitionScope(this, purpose, start);
	}

	public void RecordOutcome(string purpose, RateLimitOutcome outcome)
	{
		switch (outcome) {
			case RateLimitOutcome.Denied:
				rejections.Add(1, new KeyValuePair<string, object?>("purpose", purpose));
				break;
			case RateLimitOutcome.StoreUnavailable:
				storeFailures.Add(1, new KeyValuePair<string, object?>("purpose", purpose));
				break;
			case RateLimitOutcome.Allowed:
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown rate-limit outcome.");
		}
	}

	public void RecordRowsPruned(string purpose, int count)
	{
		if (count > 0) {
			rowsPruned.Add(count, new KeyValuePair<string, object?>("purpose", purpose));
		}
	}

	private float ReadLivePartitionCount()
	{
		using var scope = scopeFactory.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<PostgreSqlJobTrackIdentityDbContext>();
		return context.ReadRateLimitLivePartitionCount();
	}

	private sealed class AcquisitionScope(RateLimitMetrics metrics, string purpose, long start) : IDisposable
	{
		public void Dispose() =>
			metrics.acquisitionLatencyMilliseconds.Record(
				TimeProvider.System.GetElapsedTime(start).TotalMilliseconds, new KeyValuePair<string, object?>("purpose", purpose));
	}
}
