namespace JobTrack.Abstractions;

/// <summary>
///     Strongly typed identifier for an <c>audit_event</c> row (ADR 0006). Comparable so persistence
///     ports can express a keyset-pagination boundary (fresh-eyes review §2.3) as
///     <c>e.Id &lt; cursor.Id</c> -- EF Core translates relational comparisons between two values of the
///     same converted property type into the underlying column comparison, whereas <c>Value</c> member
///     access on a converted property does not translate.
/// </summary>
public readonly record struct AuditEventId(long Value) : IComparable<AuditEventId>
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;

	/// <inheritdoc />
	public int CompareTo(AuditEventId other) => Value.CompareTo(other.Value);

	/// <summary>Whether <paramref name="left" /> precedes <paramref name="right" />.</summary>
	public static bool operator <(AuditEventId left, AuditEventId right) => left.CompareTo(right) < 0;

	/// <summary>Whether <paramref name="left" /> follows <paramref name="right" />.</summary>
	public static bool operator >(AuditEventId left, AuditEventId right) => left.CompareTo(right) > 0;

	/// <summary>Whether <paramref name="left" /> precedes or equals <paramref name="right" />.</summary>
	public static bool operator <=(AuditEventId left, AuditEventId right) => left.CompareTo(right) <= 0;

	/// <summary>Whether <paramref name="left" /> follows or equals <paramref name="right" />.</summary>
	public static bool operator >=(AuditEventId left, AuditEventId right) => left.CompareTo(right) >= 0;
}
