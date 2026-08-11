namespace JobTrack.TestSupport;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Observes how many provider connections are open concurrently during one persistence operation,
///     guarding against row-proportional pool fan-out.
/// </summary>
public sealed class ConnectionConcurrencyInterceptor : DbConnectionInterceptor
{
	private int activeConnections;
	private int maximumConcurrentConnections;

	public int MaximumConcurrentConnections => Volatile.Read(ref maximumConcurrentConnections);

	public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => RecordOpenedConnection();

	public override Task ConnectionOpenedAsync(
		DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
	{
		RecordOpenedConnection();
		return Task.CompletedTask;
	}

	public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) =>
		_ = Interlocked.Decrement(ref activeConnections);

	public override Task ConnectionClosedAsync(
		DbConnection connection, ConnectionEndEventData eventData)
	{
		_ = Interlocked.Decrement(ref activeConnections);
		return Task.CompletedTask;
	}

	private void RecordOpenedConnection()
	{
		var current = Interlocked.Increment(ref activeConnections);
		var observedMaximum = Volatile.Read(ref maximumConcurrentConnections);
		while (current > observedMaximum) {
			var priorMaximum = Interlocked.CompareExchange(ref maximumConcurrentConnections, current, observedMaximum);
			if (priorMaximum == observedMaximum) {
				return;
			}

			observedMaximum = priorMaximum;
		}
	}
}
