namespace JobTrack.TestSupport;

using System.Data.Common;
using Database;

/// <summary>Installs a test-only provider trigger that rejects every subsequent audit insert.</summary>
internal static class AuditFailureInjection
{
	public static async Task InstallAsync(
		DbConnection connection,
		SchemaProvider provider,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(connection);

		await using var command = connection.CreateCommand();
		command.CommandText = provider switch {
			SchemaProvider.Sqlite => """
									 CREATE TRIGGER test_fail_audit_insert
									 BEFORE INSERT ON audit_event
									 BEGIN
									     SELECT RAISE(ABORT, 'injected audit persistence failure');
									 END;
									 """,
			SchemaProvider.PostgreSql => """
										 CREATE FUNCTION test_fail_audit_insert() RETURNS trigger
										 LANGUAGE plpgsql AS $$
										 BEGIN
										     RAISE EXCEPTION 'injected audit persistence failure';
										 END;
										 $$;

										 CREATE TRIGGER test_fail_audit_insert
										 BEFORE INSERT ON audit_event
										 FOR EACH ROW EXECUTE FUNCTION test_fail_audit_insert();
										 """,
			_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported schema provider."),
		};
		_ = await command.ExecuteNonQueryAsync(cancellationToken);
	}
}
