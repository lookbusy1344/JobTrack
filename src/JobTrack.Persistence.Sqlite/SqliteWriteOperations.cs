namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Shared;
using Shared.Ports;

/// <summary>
///     The SQLite write-path seam (ADR 0064), shared by every command port whose body lives in
///     <c>JobTrack.Persistence.Shared</c>. One <see cref="SqliteJobTrackDbContext" />/connection/
///     transaction per call, opened eagerly so the required per-connection pragmas apply
///     (docs/operations/sqlite-limitations-and-configuration.md).
/// </summary>
internal sealed class SqliteWriteOperations(string connectionString) : IProviderWriteOperations
{
	/// <summary>
	///     SQLite's <c>SQLITE_CONSTRAINT</c> primary result code (sqlite3.h): the base code shared by
	///     schema versions 0009/0010/0011's overlap triggers' <c>RAISE(ABORT, ...)</c>.
	/// </summary>
	private const int ConstraintErrorCode = 19;

	/// <summary>
	///     <c>SQLITE_CONSTRAINT_UNIQUE</c> (sqlite3.h): the extended code identifying a unique-index
	///     rejection specifically, as opposed to any other constraint sharing the base code.
	/// </summary>
	private const int UniqueConstraintErrorCode = 2067;

	/// <summary>Schema version 0007's leaf-not-closed triggers raise this message (ADR 0044).</summary>
	private const string LeafClosedMessage = "work-session-leaf-closed";

	/// <summary>Schema version 0007's no-active-sessions-on-terminal trigger raises this message (ADR 0044).</summary>
	private const string ActiveSessionsMessage = "leaf-closure-active-sessions";

	public async Task<DbContext> CreateOpenContextAsync(CancellationToken cancellationToken) =>
		await SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken).ConfigureAwait(false);

	/// <summary>Begins a write transaction that serializes against concurrent SQLite writers.</summary>
	/// <remarks>
	///     SQLite has no advisory lock, so <see cref="IsolationLevel.Serializable" /> starts a
	///     <c>BEGIN IMMEDIATE</c> transaction that serializes concurrent writers through SQLite's
	///     single-writer model.
	/// </remarks>
	public async Task<IDbContextTransaction> BeginWriteTransactionAsync(DbContext context, CancellationToken cancellationToken) =>
		await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

	public async Task<int> RevokeAllTokensForUserAsync(
		DbContext context, AppUserId userId, Instant now, CancellationToken cancellationToken) =>
		await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, userId, now, cancellationToken).ConfigureAwait(false);

	/// <summary>Classifies a SQLite write conflict from the error codes and trigger messages in the chain.</summary>
	/// <remarks>
	///     A tracked-entity <c>SaveChangesAsync</c> wraps the driver's <see cref="SqliteException" />
	///     inside a <see cref="DbUpdateException" />, hence walking the whole chain. SQLite gives a
	///     trigger no distinct extended code -- every <c>RAISE(ABORT, ...)</c> is plain
	///     <c>SQLITE_CONSTRAINT</c> -- so the two deferred-trigger kinds are told apart by the message
	///     each raises, and only what matches neither falls through to the generic overlap kind.
	/// </remarks>
	/// <inheritdoc />
	/// <remarks>
	///     <paramref name="additionallyLockedRequiredJobId" /> is unused: SQLite's write transaction is
	///     already <c>BEGIN IMMEDIATE</c>-serialized against every other writer, so there is no
	///     concurrent reopen to lock out and no per-leaf lock to take.
	/// </remarks>
	public async Task<bool> IsLeafReadyAsync(
		DbContext context, JobNodeId leafId, JobNodeId? additionallyLockedRequiredJobId, CancellationToken cancellationToken) =>
		await LeafReadiness.IsReadyAsync(context, leafId, cancellationToken).ConfigureAwait(false);

	/// <inheritdoc />
	public async Task FlushBeforeTerminalTransitionAsync(DbContext context, CancellationToken cancellationToken) =>
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

	public WriteConflictKind ClassifyWriteConflict(Exception? ex)
	{
		var kind = WriteConflictKind.None;
		for (var current = ex; current is not null; current = current.InnerException) {
			if (current is not SqliteException sqlite) {
				continue;
			}

			var candidate = sqlite switch {
				_ when sqlite.Message.Contains(LeafClosedMessage, StringComparison.Ordinal) => WriteConflictKind.LeafClosed,
				_ when sqlite.Message.Contains(ActiveSessionsMessage, StringComparison.Ordinal) => WriteConflictKind.ActiveSessions,
				_ when sqlite.SqliteExtendedErrorCode == UniqueConstraintErrorCode => WriteConflictKind.UniquenessViolation,
				_ when sqlite.SqliteErrorCode == ConstraintErrorCode => WriteConflictKind.RangeOverlap,
				_ => WriteConflictKind.None,
			};

			// Most specific wins wherever it sits in the chain, matching the separate whole-chain walks
			// the per-port Find*Violation helpers used to do.
			if (candidate != WriteConflictKind.None && (kind == WriteConflictKind.None || candidate < kind)) {
				kind = candidate;
			}
		}

		return kind;
	}
}
