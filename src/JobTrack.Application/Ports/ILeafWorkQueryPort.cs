namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetLeafWorkAsync" /> (plan §8.5 slice
///     5). Carries no actor — the query itself has no authorization gate (see
///     <see cref="GetLeafWorkRequest" />).
/// </summary>
internal interface ILeafWorkQueryPort
{
	/// <summary>Loads the leaf's current <c>LeafWork</c>.</summary>
	/// <exception cref="EntityNotFoundException">The job node has no <c>LeafWork</c> attached.</exception>
	Task<LeafWorkResult> GetLeafWorkAsync(JobNodeId jobNodeId, CancellationToken cancellationToken = default);
}
