namespace JobTrack.Identity;

/// <summary>
///     The three-way result every rate-limit check reports (ADR 0066 Stage 5,
///     docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.4): <see cref="StoreUnavailable" />
///     is distinct from <see cref="Denied" /> because the two must fail differently -- a genuine denial
///     is the existing 429 contract, but a counter-store failure must never silently fall back to
///     admitting the request, and (for login specifically) must not disclose that anything unusual
///     happened by returning a distinguishable response from an ordinary wrong-password failure.
/// </summary>
public enum RateLimitOutcome
{
	Allowed,
	Denied,
	StoreUnavailable,
}
