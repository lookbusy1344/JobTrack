namespace JobTrack.Identity;

internal sealed class RateLimitConsumeResult
{
	public bool OutAllowed { get; init; }

	public int OutRowsPruned { get; init; }

	public int OutRowsEvicted { get; init; }
}
