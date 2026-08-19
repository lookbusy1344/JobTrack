namespace JobTrack.Abstractions.CodeStyle;

/// <summary>
///     Marks a method-like declaration as a reviewed exception to the executable-line guideline
///     enforced by <c>JobTrack.ArchitectureTests.CodeStyle_MethodLength</c>: keeping the operation
///     together was accepted on purpose, not missed. The required reason carries the justification
///     alongside the declaration itself rather than in a separate allowlist, so the exception cannot
///     drift from why it was made. Do not apply this attribute without explicit user permission — this
///     is the reviewed exception, not a routine escape hatch.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
public sealed class LongMethodAttribute : Attribute
{
	/// <summary>Creates a reviewed exception carrying its required justification.</summary>
	public LongMethodAttribute(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		Reason = reason;
	}

	/// <summary>Why this declaration is exempt from the executable-line guideline.</summary>
	public string Reason { get; }
}
