namespace JobTrack.Persistence.Shared;

using NodaTime;

/// <summary>
///     Lifted out of both providers' <c>CostQueryAssembly</c> (2026-07-24
///     code-review-scalability-remediation-plan §2.6) -- textually identical, and dependent only on
///     NodaTime, not <c>JobTrack.Domain</c>/<c>JobTrack.Application</c>, so it clears this project's
///     reference-scope constraint (impl plan §7.4) that keeps the rest of each provider's
///     cost-assembly logic from moving here.
/// </summary>
internal static class SessionEndClipping
{
	/// <summary>
	///     Clips an open-ended work session's effective end to the cost read's <paramref name="asOf" />
	///     instant: a still-open session (no <paramref name="finishedAt" />) or one finishing after
	///     <paramref name="asOf" /> is costed only up to <paramref name="asOf" />, never beyond it.
	/// </summary>
	public static Instant ClipEnd(Instant? finishedAt, Instant asOf) =>
		finishedAt.HasValue && finishedAt.Value < asOf ? finishedAt.Value : asOf;
}
