namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Hierarchy;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IPrerequisiteQueryPort" /> for application-slice tests (plan
///     §7.3: "write application tests with fake ports, then provider conformance tests using real
///     databases").
/// </summary>
internal sealed class FakePrerequisiteQueryPort : IPrerequisiteQueryPort
{
	private readonly List<PrerequisiteEdge> _edges = [];
	private readonly HashSet<JobNodeId> _nodes = [];

	public int CountDirectDependentsCallCount { get; private set; }

	public int GetPrerequisitesCallCount { get; private set; }

	public Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesAsync(
		JobNodeId nodeId, int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		GetPrerequisitesCallCount++;

		if (!_nodes.Contains(nodeId)) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		var edges = _edges
			.Where(e => e.RequiredJobId == nodeId || e.DependentJobId == nodeId)
			.OrderBy(e => e.RequiredJobId.Value).ThenBy(e => e.DependentJobId.Value)
			.Skip(offset);
		return Task.FromResult<EquatableArray<PrerequisiteEdge>>([.. limit.HasValue ? edges.Take(limit.Value) : edges]);
	}

	public Task<int> CountDirectDependentsAsync(JobNodeId requiredJobId, CancellationToken cancellationToken = default)
	{
		CountDirectDependentsCallCount++;

		if (!_nodes.Contains(requiredJobId)) {
			throw new EntityNotFoundException($"Job node {requiredJobId} does not exist.");
		}

		return Task.FromResult(_edges.Count(edge => edge.RequiredJobId == requiredJobId));
	}

	public void SeedNode(JobNodeId nodeId) => _nodes.Add(nodeId);

	public void SeedEdge(PrerequisiteEdge edge)
	{
		_nodes.Add(edge.RequiredJobId);
		_nodes.Add(edge.DependentJobId);
		_edges.Add(edge);
	}
}
