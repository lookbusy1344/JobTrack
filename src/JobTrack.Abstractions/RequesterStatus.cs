namespace JobTrack.Abstractions;

/// <summary>
///     The public status vocabulary a Requester sees for their own request (ADR 0034, plan §7). Derived
///     from internal state by <c>RequesterStatusCalculator</c> — never a raw internal
///     <see cref="Achievement" /> value, which is not shown to a Requester.
/// </summary>
public enum RequesterStatus
{
	/// <summary>No requester status has been derived yet.</summary>
	None = 0,

	/// <summary>Submitted but not yet explicitly acknowledged by staff.</summary>
	Submitted = 1,

	/// <summary>Explicitly acknowledged by staff (<c>job_request.acknowledged_at</c> set), no actionable work started yet.</summary>
	Accepted = 2,

	/// <summary>At least one node in the request's subtree has work under way.</summary>
	InProgress = 3,

	/// <summary>At least one node in the request's subtree has actionable work created but not yet started.</summary>
	Waiting = 4,

	/// <summary>Every node in the request's subtree succeeded.</summary>
	Completed = 5,

	/// <summary>Every node in the request's subtree reached a terminal, non-success outcome.</summary>
	Cancelled = 6,
}
