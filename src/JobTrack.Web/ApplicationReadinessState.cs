namespace JobTrack.Web;

/// <summary>
///     ADR 0066 Stage 6: flips to draining on <c>IHostApplicationLifetime.ApplicationStopping</c>, before
///     the process actually exits, so <c>/health/ready</c> starts failing while Cloud Run still routes
///     traffic to this instance -- allowing an orchestrator to stop sending new requests and drain
///     in-flight ones within its termination window, rather than discovering the instance is gone only
///     when a request fails outright.
/// </summary>
public sealed class ApplicationReadinessState
{
	private volatile bool isDraining;

	public bool IsAcceptingTraffic => !isDraining;

	public void BeginDraining() => isDraining = true;
}
