namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetReadinessAsync" /> (plan §7.3 step
///     5). Materializes every fact the pure <see cref="Domain.Hierarchy.ReadinessCalculator" /> needs;
///     <see cref="JobQueries" /> performs no graph traversal of its own.
/// </summary>
public interface IReadinessQueryPort
{
	/// <inheritdoc cref="IJobQueries.GetReadinessAsync" />
	Task<ReadinessQueryResult> GetReadinessInputsAsync(JobNodeId nodeId, CancellationToken cancellationToken = default);
}
