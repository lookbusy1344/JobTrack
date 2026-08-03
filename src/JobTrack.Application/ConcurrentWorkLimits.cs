namespace JobTrack.Application;

/// <summary>
///     Named bounds on the session load behind <see cref="IJobQueries.GetConcurrentWorkAsync" />. The
///     query intersects one job's sessions against its workers' sessions everywhere else, so both sides
///     are capped rather than left to the size of a worker's whole history; hitting either cap sets
///     <see cref="ConcurrentWorkResult.IsTruncated" />.
/// </summary>
public static class ConcurrentWorkLimits
{
	/// <summary>Maximum number of the subject job's own sessions loaded, most recent first.</summary>
	public const int MaxSubjectSessionCount = 1_000;

	/// <summary>Maximum number of intersecting sessions on other jobs loaded, most recent first.</summary>
	public const int MaxConcurrentSessionCount = 5_000;
}
