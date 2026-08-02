namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One row of a requester's own flat request list (ADR 0033, plan §8 <c>/Requests</c>). Deliberately
///     narrow — no rates, costs, work sessions, schedules, audit internals, or unrelated siblings; see
///     <see cref="IRequestCommands.GetMyRequestsAsync" />.
/// </summary>
public sealed record JobRequestSummaryResult
{
	/// <summary>The request's anchor <c>job_node</c> identifier.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The request's description.</summary>
	public required string Description { get; init; }

	/// <summary>The instant this request was submitted.</summary>
	public required Instant SubmittedAt { get; init; }

	/// <summary>
	///     The request's public status, derived from its complete current subtree (ADR 0034). Defaults
	///     to <see cref="RequesterStatus.None" /> for source compatibility with existing initializers;
	///     every built-in provider returns a derived non-default value.
	/// </summary>
	public RequesterStatus Status { get; init; }

	/// <summary>
	///     Whether every prerequisite attached to the request anchor or any ancestor is satisfied.
	///     Composed by <see cref="IRequestCommands.GetMyRequestsAsync" /> from one batched readiness
	///     projection after the request port authorizes and returns the actor's own summaries.
	/// </summary>
	public bool IsReady { get; init; } = true;

	/// <summary>The request's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
