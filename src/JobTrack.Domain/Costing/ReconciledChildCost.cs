namespace JobTrack.Domain.Costing;

using Abstractions;

/// <summary>One child's penny-rounded displayed amount after hierarchy reconciliation (ADR 0002).</summary>
public sealed record ReconciledChildCost(JobNodeId ChildId, Money DisplayedAmount);
