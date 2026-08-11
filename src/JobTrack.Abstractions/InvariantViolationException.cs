namespace JobTrack.Abstractions;

/// <summary>
///     A materialized input or requested operation violates an invariant that should have been
///     impossible under normal write-path enforcement — e.g. the cost engine receiving a
///     same-(user, LeafWork) session overlap that could only arise from a raw write bypassing schema
///     constraints (ADR 0018). <see cref="ConstraintId" /> is a stable identifier for the specific
///     invariant, letting an operator locate and correct the underlying corruption rather than receive
///     a silently wrong result.
/// </summary>
public sealed class InvariantViolationException : JobTrackException
{
	/// <summary>Creates an <see cref="InvariantViolationException" /> with an empty <see cref="ConstraintId" />.</summary>
	public InvariantViolationException() => ConstraintId = string.Empty;

	/// <summary>Creates an <see cref="InvariantViolationException" /> with the given message and an empty <see cref="ConstraintId" />.</summary>
	public InvariantViolationException(string message)
		: base(message) =>
		ConstraintId = string.Empty;

	/// <summary>
	///     Creates an <see cref="InvariantViolationException" /> with the given message and inner exception, and an
	///     empty <see cref="ConstraintId" />.
	/// </summary>
	public InvariantViolationException(string message, Exception innerException)
		: base(message, innerException) =>
		ConstraintId = string.Empty;

	/// <summary>Creates an <see cref="InvariantViolationException" /> naming the violated invariant.</summary>
	public InvariantViolationException(string constraintId, string message)
		: base(message) =>
		ConstraintId = constraintId;

	/// <summary>Creates an <see cref="InvariantViolationException" /> naming the violated invariant, with an inner exception.</summary>
	public InvariantViolationException(string constraintId, string message, Exception innerException)
		: base(message, innerException) =>
		ConstraintId = constraintId;

	/// <summary>The stable identifier of the specific invariant that was violated.</summary>
	public string ConstraintId { get; }
}
