namespace JobTrack.Database;

using System.Data.Common;

/// <summary>
///     Provider-specific access to the <c>schema_version</c> tracking table.
///     Reading tolerates the table not existing yet (an empty database reports
///     zero applied versions rather than throwing).
/// </summary>
public interface ISchemaVersionStore
{
	Task<IReadOnlyList<AppliedSchemaVersion>> GetAppliedVersionsAsync(
		DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);

	Task RecordAppliedVersionAsync(
		DbConnection connection, DbTransaction transaction, AppliedSchemaVersion appliedVersion, CancellationToken cancellationToken);
}
