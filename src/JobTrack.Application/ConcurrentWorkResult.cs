namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Result of <see cref="IJobQueries.GetConcurrentWorkAsync" />: for one job, which other jobs its
///     own workers were simultaneously clocked on to, and for how long. Rows are grouped by worker —
///     each worker's rows are contiguous, workers ordered by descending total concurrent time — and
///     within a worker ordered by descending overlap.
/// </summary>
public sealed record ConcurrentWorkResult
{
	/// <summary>The job node this result was calculated for.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The instant unfinished sessions were bounded by, and therefore the moment this answer describes.</summary>
	public required Instant AsOf { get; init; }

	/// <summary>The overlap rows, grouped by worker.</summary>
	public required EquatableArray<ConcurrentWorkRow> Rows { get; init; }

	/// <summary>
	///     Whether the underlying session load hit <see cref="ConcurrentWorkLimits" /> and the reported
	///     totals are therefore a floor rather than the whole picture. Surfaced rather than absorbed, so
	///     a caller can say so instead of presenting a truncated total as complete.
	/// </summary>
	public required bool IsTruncated { get; init; }
}
