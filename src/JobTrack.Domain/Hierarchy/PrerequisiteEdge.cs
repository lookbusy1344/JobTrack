namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     One directed prerequisite edge (spec §6): <see cref="RequiredJobId" /> must reach derived
///     <see cref="Achievement.Success" /> before <see cref="DependentJobId" /> is eligible to proceed.
/// </summary>
public sealed record PrerequisiteEdge(JobNodeId RequiredJobId, JobNodeId DependentJobId);
