namespace JobTrack.Persistence.Sqlite;

/// <summary>
///     The PRAGMA statements every write-side connection in this project issues
///     immediately after opening (docs/operations/sqlite-limitations-and-configuration.md).
///     <c>foreign_keys</c>/<c>busy_timeout</c> are per-connection opt-ins SQLite
///     does not enforce by default; <c>journal_mode</c>/<c>synchronous</c> are
///     database-file properties that only need setting once but are reasserted
///     here defensively, matching the existing per-connection pattern.
/// </summary>
internal static class SqliteConnectionPragmas
{
	internal const string ConfigureConnectionSql =
		"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
}
