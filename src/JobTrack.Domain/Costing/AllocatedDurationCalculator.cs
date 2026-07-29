namespace JobTrack.Domain.Costing;

using Abstractions;

/// <summary>Aggregates exact concurrency-allocated segment shares into per-leaf worked durations.</summary>
public static class AllocatedDurationCalculator
{
	/// <summary>Returns each leaf's exact allocated duration without converting any share to decimal hours.</summary>
	public static IReadOnlyDictionary<JobNodeId, AllocatedDuration> ComputeLeafDurations(
		IReadOnlyCollection<SessionSegmentAllocation> allocations)
	{
		ArgumentNullException.ThrowIfNull(allocations);

		var durations = new Dictionary<JobNodeId, AllocatedDuration>();
		foreach (var allocation in allocations) {
			var duration = AllocatedDuration.FromShare(allocation.Share);
			durations[allocation.NodeId] = durations.GetValueOrDefault(allocation.NodeId, AllocatedDuration.Zero).Add(duration);
		}

		return durations;
	}
}
