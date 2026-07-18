namespace JobTrack.Domain.Rates;

using Abstractions;

/// <summary>The outcome of rate resolution (spec §9.3): the applicable hourly rate and its provenance.</summary>
public sealed record ResolvedRate(HourlyRate Rate, RateSource Source);
