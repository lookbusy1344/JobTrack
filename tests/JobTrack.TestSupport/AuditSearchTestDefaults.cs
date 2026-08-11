namespace JobTrack.TestSupport;

/// <summary>
///     A page size comfortably larger than any fixture this test suite seeds directly through
///     <c>IAuditQueryPort.SearchAuditEventsAsync</c> to verify a command wrote the audit row it
///     expected (fresh-eyes review §2.3's bounded-page contract) -- not a production paging default.
/// </summary>
public static class AuditSearchTestDefaults
{
	public const int AllRowsLimit = 100;
}
