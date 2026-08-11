namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using Shared.Ports;

/// <summary>
///     The PostgreSQL write-path seam (ADR 0064), shared by every command port whose body lives in
///     <c>JobTrack.Persistence.Shared</c>. One <see cref="PostgreSqlJobTrackDbContext" />/connection/
///     transaction per call.
/// </summary>
internal sealed class PostgreSqlWriteOperations(NpgsqlDataSource dataSource) : IProviderWriteOperations
{
	/// <summary>Schema version 0007's <c>work_session_leaf_not_closed_on_insert/on_update</c> triggers (ADR 0044).</summary>
	private const string LeafClosedSqlState = "P0007";

	/// <summary>Schema version 0007's <c>leaf_work_no_active_sessions_on_terminal</c> trigger (ADR 0044).</summary>
	private const string ActiveSessionsSqlState = "P0008";

	public Task<DbContext> CreateOpenContextAsync(CancellationToken cancellationToken)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		// Npgsql opens lazily on first command and needs no per-connection setup, so there is nothing
		// to await here -- unlike SQLite, which must open eagerly to apply its pragmas.
		return Task.FromResult<DbContext>(new PostgreSqlJobTrackDbContext(options));
	}

	public async Task<IDbContextTransaction> BeginWriteTransactionAsync(DbContext context, CancellationToken cancellationToken) =>
		await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

	public async Task<int> RevokeAllTokensForUserAsync(
		DbContext context, AppUserId userId, Instant now, CancellationToken cancellationToken) =>
		await PostgreSqlPersonalAccessTokenFunctions.RevokeAllForUserAsync(context, userId, now, cancellationToken)
			.ConfigureAwait(false);

	/// <summary>Classifies a PostgreSQL write conflict from the SQLSTATEs in the exception chain.</summary>
	/// <remarks>
	///     A GiST exclusion constraint surfaces as <c>ExclusionViolation</c>, or as
	///     <c>DeadlockDetected</c> under genuine concurrent interleaving; EF's Npgsql execution strategy
	///     re-wraps either in an outer <see cref="InvalidOperationException" /> even on a single,
	///     non-retried attempt, hence walking the whole chain. The two deferred constraint triggers
	///     carry distinct SQLSTATEs precisely so they never get confused with the overlap constraints.
	/// </remarks>
	/// <inheritdoc />
	public async Task<bool> IsLeafReadyAsync(
		DbContext context, JobNodeId leafId, JobNodeId? additionallyLockedRequiredJobId, CancellationToken cancellationToken) =>
		await LeafReadiness.IsReadyAsync(context, leafId, cancellationToken, additionallyLockedRequiredJobId).ConfigureAwait(false);

	/// <inheritdoc />
	public Task FlushBeforeTerminalTransitionAsync(DbContext context, CancellationToken cancellationToken) => Task.CompletedTask;

	public WriteConflictKind ClassifyWriteConflict(Exception? ex)
	{
		var kind = WriteConflictKind.None;
		for (var current = ex; current is not null; current = current.InnerException) {
			if (current is not PostgresException pg) {
				continue;
			}

			var candidate = pg.SqlState switch {
				LeafClosedSqlState => WriteConflictKind.LeafClosed,
				ActiveSessionsSqlState => WriteConflictKind.ActiveSessions,
				PostgresErrorCodes.UniqueViolation => WriteConflictKind.UniquenessViolation,
				PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.DeadlockDetected => WriteConflictKind.RangeOverlap,
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
