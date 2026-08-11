namespace JobTrack.Domain.Costing;

using System.Runtime.InteropServices;
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
			// Single probe (Stage 2 item 2d, large-database performance plan §4): GetValueOrDefault +
			// indexer set is two hash lookups per allocation; GetValueRefOrAddDefault is one.
			ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(durations, allocation.NodeId, out _);
			current = (current ?? AllocatedDuration.Zero).Add(duration);
		}

		return durations;
	}
}
