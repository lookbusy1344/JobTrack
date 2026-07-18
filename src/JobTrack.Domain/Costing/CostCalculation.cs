namespace JobTrack.Domain.Costing;

using Abstractions;

/// <summary>Exact hierarchy costs and their canonical explainable segment trace.</summary>
public sealed record CostCalculation(
	EquatableDictionary<JobNodeId, Money> ExactCosts,
	EquatableArray<CostSegmentTrace> Trace);
