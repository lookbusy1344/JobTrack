namespace JobTrack.Database.PerformanceTests;

using System.Globalization;

internal static class BlockedPrerequisitePlanGuard
{
	private const string DistinctRequiredScan = "CTE Scan on required";
	private const string DistinctRequiredFilter = "Filter: (NOT node_succeeded(id))";
	private const string PerEdgeFilter = "Filter: (NOT node_succeeded(from_id))";
	private const string ExecutionTimePrefix = "Execution Time:";
	private const string MillisecondSuffix = "ms";

	internal static bool HasDistinctRequiredEvaluation(string plan) =>
		plan.Contains(DistinctRequiredScan, StringComparison.Ordinal)
		&& plan.Contains(DistinctRequiredFilter, StringComparison.Ordinal)
		&& !plan.Contains(PerEdgeFilter, StringComparison.Ordinal);

	internal static TimeSpan ExecutionTime(string plan)
	{
		var executionTimeLine = plan
			.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.SingleOrDefault(static line => line.StartsWith(ExecutionTimePrefix, StringComparison.Ordinal))
			?? throw new InvalidDataException("The PostgreSQL plan did not report an execution time.");
		var valueWithUnit = executionTimeLine[ExecutionTimePrefix.Length..].Trim();
		if (!valueWithUnit.EndsWith(MillisecondSuffix, StringComparison.Ordinal)) {
			throw new InvalidDataException("The PostgreSQL plan execution time was not reported in milliseconds.");
		}

		var millisecondsText = valueWithUnit[..^MillisecondSuffix.Length].Trim();
		var milliseconds = decimal.Parse(millisecondsText, NumberStyles.Number, CultureInfo.InvariantCulture);

		return TimeSpan.FromTicks(decimal.ToInt64(milliseconds * TimeSpan.TicksPerMillisecond));
	}
}
