namespace JobTrack.Database;

using System.Data.Common;

/// <summary>
///     Orchestrates one deployment run over a dedicated ADO.NET connection:
///     applies every not-yet-applied schema-version script in order, refusing
///     to proceed on a checksum mismatch or an unknown recorded version
///     (ADR 0011). Provider-agnostic — provider identity lives entirely in the
///     injected <see cref="ISchemaVersionStore" /> and <see cref="IDeploymentLockStrategy" />.
/// </summary>
public sealed class SchemaDeployer
{
	private readonly string applicationVersion;
	private readonly string appliedBy;
	private readonly DbConnection connection;
	private readonly IDeploymentLockStrategy lockStrategy;
	private readonly ISchemaVersionStore store;

	public SchemaDeployer(
		DbConnection connection,
		ISchemaVersionStore store,
		IDeploymentLockStrategy lockStrategy,
		string applicationVersion,
		string appliedBy)
	{
		this.connection = connection;
		this.store = store;
		this.lockStrategy = lockStrategy;
		this.applicationVersion = applicationVersion;
		this.appliedBy = appliedBy;
	}

	public async Task DeployAsync(IReadOnlyList<SchemaVersionScript> scripts, CancellationToken cancellationToken)
	{
		var orderedScripts = scripts.OrderBy(script => script.Version).ToArray();

		foreach (var script in orderedScripts) {
			await ApplyIfNeededAsync(script, orderedScripts, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task ApplyIfNeededAsync(
		SchemaVersionScript script, IReadOnlyList<SchemaVersionScript> allScripts, CancellationToken cancellationToken)
	{
		// Re-check under the lock (not once for the whole run): a concurrent
		// deployment-tool run may have applied this version while this run
		// waited for the lock (§6.6 concurrent-run race).
		await using var transaction = await connection.BeginTransactionAsync(
			lockStrategy.TransactionIsolationLevel, cancellationToken).ConfigureAwait(false);

		await lockStrategy.AcquireAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

		var appliedVersions = await store.GetAppliedVersionsAsync(connection, transaction, cancellationToken)
			.ConfigureAwait(false);

		ValidateAppliedVersionsAgainstScripts(appliedVersions, allScripts);

		if (appliedVersions.Any(applied => applied.Version == script.Version)) {
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return;
		}

		await using (var command = connection.CreateCommand()) {
			command.Transaction = transaction;
			command.CommandText = script.Sql;
			_ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		var appliedVersion = new AppliedSchemaVersion {
			Version = script.Version,
			Description = script.Description,
			Checksum = script.Checksum,
			ApplicationVersion = applicationVersion,
			AppliedBy = appliedBy,
			AppliedAtUtc = DateTimeOffset.UtcNow,
		};

		await store.RecordAppliedVersionAsync(connection, transaction, appliedVersion, cancellationToken)
			.ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private static void ValidateAppliedVersionsAgainstScripts(
		IReadOnlyList<AppliedSchemaVersion> appliedVersions, IReadOnlyList<SchemaVersionScript> scripts)
	{
		var highestKnownVersion = scripts.Count == 0 ? 0 : scripts[^1].Version;
		var scriptsByVersion = scripts.ToDictionary(script => script.Version);

		foreach (var applied in appliedVersions) {
			if (applied.Version > highestKnownVersion) {
				throw new SchemaDeploymentException(
					$"The database has schema version {applied.Version} applied, which is newer than the " +
					$"highest known script version {highestKnownVersion}. Refusing to proceed (ADR 0011).");
			}

			if (scriptsByVersion.TryGetValue(applied.Version, out var script) &&
				!string.Equals(applied.Checksum, script.Checksum, StringComparison.Ordinal)) {
				throw new SchemaDeploymentException(
					$"Schema version {applied.Version} was recorded with checksum '{applied.Checksum}' but the " +
					$"on-disk script '{script.FilePath}' now computes '{script.Checksum}'. A merged script must " +
					"never be edited after being applied (ADR 0011).");
			}
		}
	}
}
