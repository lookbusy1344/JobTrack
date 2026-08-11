namespace JobTrack.Abstractions;

/// <summary>
///     The four job priority levels, in escalating order (ADR 0021). Values match the seeded
///     <c>priority</c> reference-table ids exactly (database schema version 0004).
/// </summary>
public enum Priority
{
	/// <summary>The priority has not been specified.</summary>
	Unspecified = 0,

	/// <summary>Business as usual; no particular urgency.</summary>
	Low = 1,

	/// <summary>The default priority level.</summary>
	Medium = 2,

	/// <summary>Needs attention soon.</summary>
	High = 3,

	/// <summary>Needs immediate attention.</summary>
	Urgent = 4,
}
