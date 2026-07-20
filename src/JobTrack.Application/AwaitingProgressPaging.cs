namespace JobTrack.Application;

/// <summary>
///     Named page-size bounds for <see cref="IJobQueries.GetAwaitingProgressAsync" />. An omitted
///     limit uses <see cref="DefaultPageSize" />, and an excessive limit is clamped to
///     <see cref="MaxPageSize" />, so every caller receives a bounded result.
/// </summary>
public static class AwaitingProgressPaging
{
	/// <summary>The page size used when a request omits its limit.</summary>
	public const int DefaultPageSize = 50;

	/// <summary>The largest page size returned by one request.</summary>
	public const int MaxPageSize = 200;
}
