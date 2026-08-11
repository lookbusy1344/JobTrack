namespace JobTrack.Identity;

using System.Collections.Frozen;
using Npgsql;

/// <summary>
///     Rejects a PostgreSQL connection string that would send credentials and application data to a
///     remote host over an unauthenticated, unencrypted, or self-signed-tolerant channel (security
///     review remediation §2.9). Npgsql's own default, <c>SSL Mode=Prefer</c>, neither guarantees
///     encryption nor authenticates the server; a private network limits exposure but does not
///     replace transport authentication. A same-host Unix-domain socket or loopback TCP connection is
///     exempt -- the reverse-proxy/database topology already keeps that traffic host-local (ADR
///     0014's single-server deployment).
/// </summary>
/// <remarks>
///     Lives here, not <c>JobTrack.Persistence.PostgreSql</c>, because that project's public surface
///     is deliberately limited to its one client-factory type
///     (<c>PersistencePublicSurfaceTests.Persistence_assemblies_export_only_their_client_factory</c>).
///     <c>JobTrack.Identity</c> is the composed-by-hosts-only adapter tier both <c>JobTrack.Web</c>
///     and <c>JobTrack.AdminCli</c> already reference and that already depends on Npgsql.
///     Deliberately duplicated (not shared) in <c>JobTrack.Database</c>'s own copy of this type: that
///     project sits below the reusable-library layer in the mandatory database → library → HTTP API
///     → web ordering and has "no EF Core or domain dependency" by design, so it cannot reference
///     this project either. Keep all copies' logic identical if any changes.
/// </remarks>
public static class PostgreSqlTransportSecurity
{
	private static readonly FrozenSet<string> LoopbackHosts =
		FrozenSet.ToFrozenSet(["localhost", "127.0.0.1", "::1"], StringComparer.OrdinalIgnoreCase);

	/// <summary>
	///     Throws <see cref="InvalidOperationException" /> if <paramref name="connectionString" /> targets a
	///     remote host without an authenticated, encrypted channel. Local Unix-domain-socket and
	///     loopback-TCP connections are exempt regardless of <c>SSL Mode</c>.
	/// </summary>
	public static void Validate(string connectionString)
	{
		ArgumentNullException.ThrowIfNull(connectionString);

		var builder = new NpgsqlConnectionStringBuilder(connectionString);
		if (IsLocal(builder.Host)) {
			return;
		}

		if (builder.SslMode is not SslMode.VerifyFull) {
			throw new InvalidOperationException(
				$"PostgreSQL connection to remote host '{builder.Host}' must set 'SSL Mode=VerifyFull' " +
				$"with a trusted root certificate; found '{builder.SslMode}'. " +
				"'Trust Server Certificate=true' is not an acceptable substitute for a remote host.");
		}

		if (string.IsNullOrWhiteSpace(builder.RootCertificate)) {
			throw new InvalidOperationException(
				$"PostgreSQL connection to remote host '{builder.Host}' sets 'SSL Mode={builder.SslMode}' but no " +
				"'Root Certificate' -- a trusted CA file is required to authenticate the server.");
		}
	}

	private static bool IsLocal(string? host) =>
		string.IsNullOrEmpty(host) || host.StartsWith('/') || LoopbackHosts.Contains(host);
}
