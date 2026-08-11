namespace JobTrack.Persistence.PostgreSql;

/// <summary>
///     PostgreSQL advisory-lock domain names for this provider (ADR 0012). Mirrors
///     <c>JobTrack.Database.PostgreSqlLockKeys</c>'s pattern; cannot reference that internal type
///     directly, since persistence providers depend only on <c>JobTrack.Application</c>.
/// </summary>
internal static class PostgreSqlLockKeys
{
	/// <summary>One fixed, well-known key serializing concurrent bootstrap attempts (ADR 0015).</summary>
	public const string Bootstrap = "jobtrack:bootstrap";
}
