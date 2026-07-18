namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     One unsatisfied prerequisite blocking readiness (spec §6): the required job that has not
///     reached derived <see cref="Achievement.Success" />, and the node — the checked leaf itself or
///     one of its ancestors — on which the prerequisite edge was declared. This is the inherited
///     blocker's origin that readiness diagnostics must identify.
/// </summary>
public sealed record UnsatisfiedPrerequisite(JobNodeId RequiredJobId, JobNodeId DeclaredOnJobId);
