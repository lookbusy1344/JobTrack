namespace JobTrack.Persistence.Shared.Ports;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;

/// <summary>
///     The write-path operations every shared command port needs and no provider can express
///     neutrally. One implementation per provider, shared by all of them.
/// </summary>
/// <remarks>
///     Keeping the seam an explicit interface rather than a set of overrides is deliberate (ADR 0064):
///     these three members are the whole write-path difference between PostgreSQL and SQLite. A member
///     added here is a new divergence and wants justifying.
/// </remarks>
internal interface IProviderWriteOperations
{
	/// <summary>Opens a fresh context with its connection open and any per-connection setup already applied.</summary>
	Task<DbContext> CreateOpenContextAsync(CancellationToken cancellationToken);

	/// <summary>
	///     Begins the transaction a compound write commits through. PostgreSQL takes the provider
	///     default and serializes on its advisory locks; SQLite has none, so
	///     <see cref="System.Data.IsolationLevel.Serializable" /> starts a <c>BEGIN IMMEDIATE</c> that
	///     serializes concurrent writers through its single-writer model.
	/// </summary>
	Task<IDbContextTransaction> BeginWriteTransactionAsync(DbContext context, CancellationToken cancellationToken);

	/// <summary>
	///     Revokes every live personal access token for <paramref name="userId" />, returning how many
	///     were revoked. PostgreSQL calls its source-controlled revoke function; SQLite uses the shared
	///     EF implementation.
	/// </summary>
	Task<int> RevokeAllTokensForUserAsync(DbContext context, AppUserId userId, Instant now, CancellationToken cancellationToken);

	/// <summary>
	///     Classifies what write conflict, if any, <paramref name="ex" />'s inner-exception chain
	///     reports. PostgreSQL reads SQLSTATEs; SQLite reads extended error codes and, where its
	///     triggers give no distinct code, the <c>RAISE(ABORT, ...)</c> message.
	/// </summary>
	/// <remarks>
	///     Implementations walk the whole chain -- the outer wrapper differs by provider and by whether
	///     the write went through a tracked-entity <c>SaveChangesAsync</c> -- and return the most
	///     specific kind found anywhere in it, so a call site's ordered <c>catch</c> filters mean the
	///     same thing on both providers.
	/// </remarks>
	WriteConflictKind ClassifyWriteConflict(Exception? ex);

	/// <summary>
	///     The in-transaction prerequisite recheck (spec §6: "the start... command shall recheck
	///     prerequisites inside their write transaction"), evaluated against what
	///     <paramref name="context" />'s open transaction can see.
	/// </summary>
	/// <remarks>
	///     The readiness decision itself is identical on both providers -- the same
	///     <c>ReadinessCalculator</c> over the same hierarchy facts. What differs is serialization
	///     against a concurrent reopen of a required job: PostgreSQL takes ADR 0012's per-leaf advisory
	///     lock for each required job (including
	///     <paramref name="additionallyLockedRequiredJobId" />, which a reopen must lock even though it
	///     is not one of its own prerequisites), while SQLite's single-writer transaction already
	///     serializes every writer and so takes none.
	/// </remarks>
	Task<bool> IsLeafReadyAsync(
		DbContext context, JobNodeId leafId, JobNodeId? additionallyLockedRequiredJobId, CancellationToken cancellationToken);

	/// <summary>
	///     Makes the pending session-finish writes visible to the database before a leaf's terminal
	///     achievement transition is applied in the same transaction.
	/// </summary>
	/// <remarks>
	///     SQLite's <c>leaf-closure-active-sessions</c> trigger is immediate: it evaluates the moment
	///     each statement runs, so the finish rows must reach the table before the achievement
	///     <c>UPDATE</c> or the trigger still sees an active session. An extra
	///     <c>SaveChangesAsync</c> inside the one open transaction gives it that write order while
	///     preserving atomicity -- a failure on either call still rolls the whole transaction back.
	///     PostgreSQL's equivalent trigger is deferred to commit, by which time both writes are
	///     present, so it does nothing rather than spend a round trip.
	/// </remarks>
	Task FlushBeforeTerminalTransitionAsync(DbContext context, CancellationToken cancellationToken);
}
