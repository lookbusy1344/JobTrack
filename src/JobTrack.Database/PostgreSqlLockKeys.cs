namespace JobTrack.Database;

/// <summary>
///     PostgreSQL advisory-lock domain names (ADR 0012). Each is a fixed
///     namespace constant passed to <c>hashtext()</c> to derive the actual
///     64-bit lock key — defined once here, never restated as a literal at a
///     call site.
/// </summary>
internal static class PostgreSqlLockKeys
{
	/// <summary>
	///     One fixed, well-known key (no entity id) serializing concurrent
	///     deployment-tool runs (ADR 0011, ADR 0012).
	/// </summary>
	public const string SchemaDeployment = "jobtrack:schema-deployment";
}
