namespace JobTrack.Abstractions;

/// <summary>Strongly typed identifier for a <c>job_request_note</c> row (ADR 0034).</summary>
public readonly record struct JobRequestNoteId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
