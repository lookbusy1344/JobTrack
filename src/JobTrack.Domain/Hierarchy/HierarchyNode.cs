namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     The materialized structural facts about one <c>job_node</c> (spec §4.1/§4.2) needed to classify
///     it and to derive recursive achievement: its parent (null only for the root), its direct
///     children, and its owned <c>LeafWork</c> achievement, if any.
/// </summary>
public sealed record HierarchyNode(JobNodeId Id, JobNodeId? ParentId, EquatableArray<JobNodeId> ChildIds, Achievement? LeafAchievement);
