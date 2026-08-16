namespace JobTrack.Abstractions;

/// <summary>
///     Marks a struct/record struct as a reviewed exception to the house 24-byte size guideline
///     enforced by <c>JobTrack.ArchitectureTests.CodeStyle_StructSize</c>: its copy cost was accepted on
///     purpose, not missed. <paramref name="reason" /> carries the justification alongside the type
///     itself rather than in a separate allowlist, so the exception cannot drift from why it was made.
///     Do not apply this attribute without explicit user permission — get agreement before exempting
///     a type from the size guideline; this is the reviewed exception, not a routine escape hatch.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class LargeStructAttribute(string reason) : Attribute
{
	/// <summary>Why this type is exempt from the 24-byte guideline.</summary>
	public string Reason { get; } = reason;
}
