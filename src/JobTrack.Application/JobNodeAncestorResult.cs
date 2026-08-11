namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     One entry of a node's breadcrumb, as returned by <see cref="IJobQueries.GetJobNodeAsync" />'s
///     <see cref="JobNodeDetailResult.Ancestors" /> — root-first, and always reflecting real ancestry
///     regardless of any archive filter applied to sibling browsing (spec: "Archived ancestors remain
///     traversable for reporting and authorization").
/// </summary>
public sealed record JobNodeAncestorResult(JobNodeId Id, string Description, NodeKind Kind);
