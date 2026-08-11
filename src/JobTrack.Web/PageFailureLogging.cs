namespace JobTrack.Web;

/// <summary>
///     Shared logging for a concurrency conflict a Razor Page's <c>OnPost*</c> handler catches and
///     recovers from (mirroring <see cref="JobTrackApi" />'s <c>ExecuteAsync</c> logging on the API
///     side): the reader sees only "someone else changed this since the form was loaded," but a
///     repeated or unexpected conflict can indicate a stale-version bug rather than routine concurrent
///     editing, and the underlying message (which node, which row) is otherwise lost.
/// </summary>
internal static partial class PageFailureLogging
{
	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "page_concurrency_conflict correlation_id={CorrelationId} page={Page}")]
	internal static partial void LogConcurrencyConflict(ILogger logger, Guid correlationId, string page, Exception exception);
}
