namespace JobTrack.Identity;

/// <summary>
///     The external API limiter's storage seam (ADR 0066 Stage 5), mirroring
///     <see cref="ILoginAttemptRateLimiter" />: <c>JobTrack.Web.InProcessApiRateLimitStore</c>
///     (<c>SingleInstance</c>) and <see cref="PostgreSqlApiRateLimitStore" /> (<c>MultiInstance</c>)
///     both back <c>Program.cs</c>'s rate-limit middleware, so the API's attachment point and 429
///     response shape stay identical regardless of topology. Declared here rather than
///     <c>JobTrack.Web</c> for the same reason as <see cref="ILoginAttemptRateLimiter" /> -- see that
///     type's doc comment.
/// </summary>
public interface IApiRateLimitStore
{
	ValueTask<RateLimitOutcome> TryAcquireAsync(string partitionKey, CancellationToken cancellationToken);
}
