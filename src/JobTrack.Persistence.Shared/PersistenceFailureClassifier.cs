namespace JobTrack.Persistence.Shared;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     Classifies a database write failure into <see cref="PersistenceFailure" /> from the
///     provider-agnostic <see cref="DbException" /> SQLSTATE surface alone (2.2). PostgreSQL carries
///     SQLSTATE (class 40 is a transient rollback, class 23 and the project's <c>P00xx</c> trigger
///     codes are integrity). SQLite has no SQLSTATE and requires classification by concrete SQLite
///     result code in its provider seam.
/// </summary>
internal static class PersistenceFailureClassifier
{
	private const string SerializationFailureSqlState = "40001";
	private const string DeadlockDetectedSqlState = "40P01";

	/// <summary>
	///     Whether <paramref name="exception" /> is a transient database write failure (SQLSTATE class 40
	///     -- deadlock or serialization --), so a call site surfaces a
	///     <see cref="Abstractions.TransientPersistenceException" /> rather than an invariant (2.2).
	/// </summary>
	public static bool IsTransientWriteFailure(Exception exception) => ClassifyWriteFailure(exception) is PersistenceFailure.Transient;

	/// <summary>
	///     Whether <paramref name="exception" /> is an integrity write failure (SQLSTATE class 23, the
	///     project's <c>P00xx</c> trigger codes), so a call site translates it to
	///     its own invariant. Anything that is neither transient nor integrity must propagate unwrapped.
	/// </summary>
	public static bool IsIntegrityWriteFailure(Exception exception) => ClassifyWriteFailure(exception) is PersistenceFailure.Integrity;

	/// <summary>
	///     Classifies a write failure only when <paramref name="exception" /> is an EF or provider write
	///     exception (a <see cref="DbUpdateException" /> or a raw <see cref="DbException" /> from a
	///     deferred constraint); any other exception is <see cref="PersistenceFailure.Unknown" /> so an
	///     unrelated fault in the write body is never mistranslated as an integrity violation.
	/// </summary>
	internal static PersistenceFailure ClassifyWriteFailure(Exception exception) =>
		exception is DbUpdateException or DbException ? Classify(exception) : PersistenceFailure.Unknown;

	/// <summary>
	///     Walks the exception chain and returns the classification of the first <see cref="DbException" />
	///     it finds, or <see cref="PersistenceFailure.Unknown" /> when the chain holds none.
	/// </summary>
	internal static PersistenceFailure Classify(Exception? exception)
	{
		for (var current = exception; current is not null; current = current.InnerException) {
			if (current is DbException db) {
				return ClassifyOne(db);
			}
		}

		return PersistenceFailure.Unknown;
	}

	private static PersistenceFailure ClassifyOne(DbException db)
	{
		if (db.SqlState is { Length: >= 2 } sqlState) {
			return ClassifyBySqlState(sqlState);
		}

		return PersistenceFailure.Unknown;
	}

	private static PersistenceFailure ClassifyBySqlState(string sqlState)
	{
		if (sqlState is SerializationFailureSqlState or DeadlockDetectedSqlState) {
			return PersistenceFailure.Transient;
		}

		return sqlState.AsSpan(0, 2) switch {
			"23" => PersistenceFailure.Integrity,
			"P0" => PersistenceFailure.Integrity,
			_ => PersistenceFailure.Unknown,
		};
	}
}
