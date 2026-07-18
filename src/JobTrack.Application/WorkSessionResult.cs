namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Result of every work-session command in <see cref="IWorkCommands" />.</summary>
public sealed record WorkSessionResult
{
	/// <summary>The session's <c>work_session</c> identifier.</summary>
	public required WorkSessionId Id { get; init; }

	/// <summary>The leaf this session is work against (<c>leaf_work_id</c>, the leaf's <c>job_node_id</c>).</summary>
	public required JobNodeId LeafWorkId { get; init; }

	/// <summary>The employee who performed this session's work.</summary>
	public required AppUserId WorkedByUserId { get; init; }

	/// <summary>The instant this session started.</summary>
	public required Instant StartedAt { get; init; }

	/// <summary>The instant this session finished, if it has finished.</summary>
	public Instant? FinishedAt { get; init; }

	/// <summary>The instant this session was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The session's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
