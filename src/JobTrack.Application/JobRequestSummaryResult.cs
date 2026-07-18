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

	/// <summary>The request's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
