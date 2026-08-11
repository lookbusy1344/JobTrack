namespace JobTrack.Abstractions;

/// <summary>Strongly typed identifier for a <c>schedule_exception</c> row (ADR 0006).</summary>
public readonly record struct ScheduleExceptionId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
