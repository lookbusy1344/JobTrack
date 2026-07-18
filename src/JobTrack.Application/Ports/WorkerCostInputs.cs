namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Costing;
using Domain.Intervals;
using Domain.Rates;
using Domain.Schedules;

/// <summary>
///     One worker's complete immutable cost inputs (plan §6.5: "complete immutable inputs for the cost
///     engine"), ready for <see cref="Domain.Costing.CostSegmentPartitioner" /> and
///     <see
///         cref="Domain.Costing.CostEngine" />
///     . <see cref="Sessions" /> includes every one of this worker's
///     sessions across the entire database — not only sessions under the requested node — because
///     correct concurrency-divisor computation requires the worker's true database-wide overlap (ADR
///     0017); <see cref="CostEngine" /> itself is responsible for never exposing a foreign session's
///     identity in its output.
/// </summary>
public sealed record WorkerCostInputs
{
	/// <summary>Every one of this worker's sessions database-wide, already eligible-clipped (spec §10.1).</summary>
	public required EquatableArray<CostableSession> Sessions { get; init; }

	/// <summary>This worker's normalized effective working intervals over the query bounds.</summary>
	public required EquatableArray<WorkInterval> EffectiveWorkingIntervals { get; init; }

	/// <summary>
	///     This worker's base scheduled working intervals over the query bounds, before schedule
	///     exceptions — used by <see cref="Domain.Costing.CostEngine" /> to stamp working-time
	///     eligibility on segment traces.
	/// </summary>
	public required EquatableArray<WorkInterval> ScheduledWorkingIntervals { get; init; }

	/// <summary>This worker's schedule exceptions overlapping the query bounds.</summary>
	public required EquatableArray<ScheduleExceptionEntry> Exceptions { get; init; }

	/// <summary>Node rate overrides declared for this worker on any node.</summary>
	public required EquatableArray<NodeRateOverride> NodeOverrides { get; init; }

	/// <summary>This worker's effective-dated user cost rates.</summary>
	public required EquatableArray<UserCostRate> UserCostRates { get; init; }

	/// <summary>This worker's default hourly rate, if any.</summary>
	public required HourlyRate? UserDefaultRate { get; init; }
}
