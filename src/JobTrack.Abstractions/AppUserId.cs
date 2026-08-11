namespace JobTrack.Abstractions;

/// <summary>Strongly typed identifier for an <c>app_user</c> row (ADR 0006).</summary>
public readonly record struct AppUserId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
