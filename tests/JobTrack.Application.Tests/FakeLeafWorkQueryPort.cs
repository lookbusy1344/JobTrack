namespace JobTrack.Application.Tests;

using Abstractions;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="ILeafWorkQueryPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases").
/// </summary>
internal sealed class FakeLeafWorkQueryPort : ILeafWorkQueryPort
{
	private readonly Dictionary<JobNodeId, LeafWorkResult> _leafWork = [];

	public Task<LeafWorkResult> GetLeafWorkAsync(JobNodeId jobNodeId, CancellationToken cancellationToken = default) =>
		_leafWork.TryGetValue(jobNodeId, out var leafWork)
			? Task.FromResult(leafWork)
			: throw new EntityNotFoundException($"Job node {jobNodeId} has no LeafWork attached.");

	public void Seed(LeafWorkResult leafWork) => _leafWork[leafWork.JobNodeId] = leafWork;
}
