namespace JobTrack.Identity;

/// <summary>
///     The login limiter's storage seam (ADR 0066 Stage 5): <c>JobTrack.Web.LoginAttemptRateLimiter</c>
///     (in-process, <c>SingleInstance</c> topology, unchanged by Stage 5) and
///     <see cref="PostgreSqlLoginAttemptRateLimiter" /> (shared, <c>MultiInstance</c>) both implement
///     this so <c>Login.cshtml.cs</c> depends on neither concrete type. Declared here, not in
///     <c>JobTrack.Web</c>, because the PostgreSQL implementation needs
///     <see cref="PostgreSqlJobTrackIdentityDbContext" />, which only <c>JobTrack.Identity</c> and
///     allowlisted <c>JobTrack.Web</c> composition files may reference
///     (
///     <c>
///         WebHostSecurityArchitectureTests.JobTrackIdentityDbContext_is_only_used_at_composition_
///         identity_and_allowlisted_pages
///     </c>
///     ). The context maps <c>rate_limit_try_consume</c> as an EF
///     table-valued function, so neither limiter embeds SQL at its application-facing call site.
/// </summary>
public interface ILoginAttemptRateLimiter
{
	ValueTask<RateLimitOutcome> TryAcquireAsync(string partitionKey, string backstopKey, CancellationToken cancellationToken);
}
