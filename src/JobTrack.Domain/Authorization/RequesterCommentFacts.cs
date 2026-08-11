namespace JobTrack.Domain.Authorization;

/// <summary>The independent facts <see cref="RequesterAccessPolicy.CanCommentAsRequester" /> evaluates.</summary>
public sealed record RequesterCommentFacts
{
	/// <summary>The visibility facts also governing whether the actor may view this request.</summary>
	public required RequesterVisibilityFacts Visibility { get; init; }

	/// <summary>Whether the request is still open to requester comment (not yet closed to the requester).</summary>
	public required bool IsOpenToRequester { get; init; }
}
