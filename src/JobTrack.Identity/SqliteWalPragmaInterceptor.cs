namespace JobTrack.Identity;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Sets <c>journal_mode = WAL</c>/<c>synchronous = NORMAL</c> on every connection this
///     <see cref="JobTrackIdentityDbContext" /> opens. <see cref="Microsoft.Data.Sqlite.SqliteConnectionStringBuilder" />
///     has connection-string keywords for <c>foreign_keys</c>/<c>busy_timeout</c> (used in
///     <see cref="ServiceCollectionExtensions" />) but none for these two, so they need an explicit
///     <c>PRAGMA</c> here instead -- independently of <c>JobTrack.Persistence.Sqlite</c>'s equivalent
///     pragma, per the same "every ad hoc SQLite connection needs the same treatment" rule that
///     applies to <c>foreign_keys</c>/<c>busy_timeout</c>.
/// </summary>
internal sealed class SqliteWalPragmaInterceptor : DbConnectionInterceptor
{
	private const string PragmaSql = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";

	public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
	{
		using var command = connection.CreateCommand();
		command.CommandText = PragmaSql;
		_ = command.ExecuteNonQuery();
	}

	public override async Task ConnectionOpenedAsync(
		DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = PragmaSql;
		_ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}
}
