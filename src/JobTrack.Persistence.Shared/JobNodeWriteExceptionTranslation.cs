namespace JobTrack.Persistence.Shared;

using System.Data.Common;
using Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
///     Translates the exceptions a tracked-entity <c>job_node</c> write can raise (impl plan §7.4:
///     "translate constraint failures to stable public exceptions... a pre-check and a database race
///     must produce the same public error category") into the stable <see cref="JobTrackException" />
///     categories the job-node command port's callers expect. Wraps both <c>SaveChangesAsync</c> and
///     the following <c>CommitAsync</c> in one translated call: PostgreSQL's leaf/branch-exclusivity
///     and hierarchy-acyclicity constraints are <c>DEFERRABLE INITIALLY DEFERRED</c> (schema versions
///     0005/0006), so they are checked at <c>COMMIT</c>, not at the write itself, and raise a raw
///     provider <see cref="DbException" /> there rather than an EF-wrapped <see cref="DbUpdateException" />.
///     <see cref="DbUpdateException" />/<see cref="DbUpdateConcurrencyException" />/<see cref="DbException" />
///     are all provider-agnostic types, so this translation is identical for both providers and lives
///     here rather than being duplicated.
/// </summary>
public static class JobNodeWriteExceptionTranslation
{
	/// <summary>
	///     Saves changes and commits for an edit/archive/create write, translating a concurrency-token
	///     mismatch and any other constraint violation (including one raised only at commit by a
	///     deferred trigger, e.g. leaf/branch exclusivity) into stable exceptions.
	///     <paramref
	///         name="afterSave" />
	///     , if given, runs after the first <c>SaveChangesAsync</c> -- letting a
	///     caller queue an audit event that needs a database-generated identifier only that first save
	///     produces (impl plan §7.3: "emits audit intent, and commits once") -- followed by one more
	///     <c>SaveChangesAsync</c> to persist it, all inside this same try/catch and transaction.
	/// </summary>
	public static async Task SaveChangesAndCommitAsync(
		DbContext context, IDbContextTransaction transaction, CancellationToken cancellationToken,
		Func<CancellationToken, Task>? afterSave = null)
	{
		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			if (afterSave is not null) {
				await afterSave(cancellationToken).ConfigureAwait(false);
				_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			}

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				"The job node was modified concurrently; re-read its current state and retry.", ex);
		}
		catch (Exception ex) when (ex is DbUpdateException or DbException) {
			throw new InvariantViolationException(
				"job-node-write-rejected", "This write violates a job-node structural invariant.", ex);
		}
	}

	/// <summary>
	///     Saves changes and commits for a delete, translating a concurrency-token mismatch the same way
	///     as <see cref="SaveChangesAndCommitAsync" /> but mapping any other constraint violation (the
	///     <c>ON DELETE RESTRICT</c> foreign keys from children, <c>leaf_work</c>, and
	///     <c>job_prerequisite</c>, or the permanent root's undeletable-guard trigger) to the stable
	///     "not deletable" invariant spec §4.6 defines for planning-node deletion.
	/// </summary>
	public static async Task SaveChangesAndCommitForDeleteAsync(
		DbContext context, IDbContextTransaction transaction, CancellationToken cancellationToken)
	{
		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				"The job node was modified concurrently; re-read its current state and retry.", ex);
		}
		catch (Exception ex) when (ex is DbUpdateException or DbException) {
			throw new InvariantViolationException(
				"job-node-not-deletable", "This job node cannot be deleted because it has dependent data.", ex);
		}
	}

	/// <summary>
	///     Saves changes and commits for attaching <c>LeafWork</c>, translating a concurrency-token
	///     mismatch the same way as <see cref="SaveChangesAndCommitAsync" /> but mapping any other
	///     constraint violation — most notably <c>leaf_work</c>'s primary-key uniqueness, when a
	///     concurrent attach to the same node wins the race the application-side existence pre-check
	///     cannot fully close — to the same "already attached" category the pre-check itself reports
	///     (impl plan §7.4: "a pre-check and a database race must produce the same public error
	///     category").
	/// </summary>
	public static async Task SaveChangesAndCommitForLeafWorkAttachAsync(
		DbContext context, IDbContextTransaction transaction, CancellationToken cancellationToken)
	{
		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				"The job node was modified concurrently; re-read its current state and retry.", ex);
		}
		catch (Exception ex) when (ex is DbUpdateException or DbException) {
			throw new InvariantViolationException("leaf-work-already-attached", "This node already has LeafWork attached.", ex);
		}
	}

	/// <summary>
	///     Runs a multi-step write spanning several intermediate <c>SaveChangesAsync</c> calls (e.g.
	///     decomposition's reparent-then-repoint-then-delete sequence, impl plan §7.3 step 4) and then
	///     commits, translating a concurrency-token mismatch or any other constraint violation raised by
	///     <em>any</em> of those intermediate calls the same way as <see cref="SaveChangesAndCommitAsync" />
	///     — not just the final one. A losing transaction in a concurrent-write race can hit the
	///     concurrency token on an intermediate step (e.g. deleting a row another transaction already
	///     committed the deletion of) just as easily as on the last one.
	/// </summary>
	public static async Task<T> RunAndCommitAsync<T>(
		IDbContextTransaction transaction, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
	{
		try {
			var result = await operation(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return result;
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				"The job node was modified concurrently; re-read its current state and retry.", ex);
		}
		catch (Exception ex) when (ex is DbUpdateException or DbException) {
			throw new InvariantViolationException(
				"job-node-write-rejected", "This write violates a job-node structural invariant.", ex);
		}
	}
}
