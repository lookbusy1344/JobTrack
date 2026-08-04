namespace JobTrack.Persistence.Shared;

/// <summary>
///     The fixed <c>audit_event.reason</c> strings the work-session write paths record. They are shared
///     rather than restated per call site because they are matched on, not merely displayed: an auditor
///     (and the contract tests) tell an automatic transition from a human-chosen one by this exact text,
///     so a provider or a caller drifting from it would silently break that distinction.
/// </summary>
internal static class WorkAuditReasons
{
	/// <summary>
	///     ADR 0038's auto-advance: the <see cref="Abstractions.Achievement.Waiting" /> -&gt;
	///     <see cref="Abstractions.Achievement.InProgress" /> transition a session start applies on the
	///     caller's behalf, whether the session starts on an existing leaf or on one being created.
	/// </summary>
	public const string AutoAdvancedOnSessionStart = "Advanced automatically on session start";
}
