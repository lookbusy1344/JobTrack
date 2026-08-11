namespace JobTrack.Abstractions;

/// <summary>Strongly typed identifier for a <c>personal_access_token</c> row (ADR 0006, ADR 0029).</summary>
public readonly record struct PersonalAccessTokenId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
