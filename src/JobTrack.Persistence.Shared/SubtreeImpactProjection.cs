namespace JobTrack.Persistence.Shared;

using Abstractions;
using Application;

/// <summary>
///     Maps the provider-neutral <see cref="SubtreeImpactData" /> onto the public
///     <see cref="SubtreeImpactResult" /> (ADR 0061).
/// </summary>
internal static class SubtreeImpactProjection
{
	/// <summary>Projects <paramref name="data" /> onto its public shape.</summary>
	public static SubtreeImpactResult ToResult(SubtreeImpactData data) => new() {
		RootId = data.RootId,
		Nodes = EquatableArray.CopyOf(data.Nodes.Select(n => new SubtreeImpactNode {
			Id = n.Id,
			ParentId = n.ParentId,
			Depth = n.Depth,
			Description = n.Description,
			Kind = n.Kind,
			Achievement = n.Achievement,
			WorkSessionCount = n.WorkSessionCount,
			IsArchived = n.IsArchived,
		}).ToArray()),
		NodeCount = data.NodeCount,
		LeafWorkCount = data.LeafWorkCount,
		WorkSessionCount = data.WorkSessionCount,
		TotalWorkedDuration = data.TotalWorkedDuration,
		InternalPrerequisiteEdgeCount = data.InternalPrerequisiteEdgeCount,
		ExternalPrerequisiteEdges = EquatableArray.CopyOf(data.ExternalPrerequisiteEdges.Select(e => new SubtreeImpactPrerequisiteEdge {
			FromId = e.FromId,
			ToId = e.ToId,
			ExternalDescription = e.ExternalDescription,
			ExternalNodeIsDependent = e.ExternalNodeIsDependent,
		}).ToArray()),
		JobRequestCount = data.JobRequestCount,
		BlockingHoldingAreas =
			EquatableArray.CopyOf(data.BlockingHoldingAreas
									  .Select(h => new SubtreeImpactHoldingArea {
										  Id = h.Id,
										  Name = h.Name,
										  JobNodeId = h.JobNodeId,
									  }).ToArray()),
		IsPermanentRoot = data.IsPermanentRoot,
	};
}
