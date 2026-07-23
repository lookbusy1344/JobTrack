namespace JobTrack.Application;

/// <summary>Result of <see cref="IWorkCommands.FinishSessionAndUpdateWriteUpAsync" />.</summary>
public sealed record FinishSessionAndUpdateWriteUpResult
{
	/// <summary>The finished session.</summary>
	public required WorkSessionResult Session { get; init; }

	/// <summary>
	///     Whether the request's <c>WriteUpChange</c> actually changed the stored write-up text --
	///     always <see langword="false" /> when no write-up change was requested, or when the submitted
	///     text already matched what was stored.
	/// </summary>
	public required bool WriteUpChanged { get; init; }

	/// <summary>The leaf's node after this finish, when a write-up change was supplied; otherwise <see langword="null" />.</summary>
	public JobNodeResult? Node { get; init; }
}
