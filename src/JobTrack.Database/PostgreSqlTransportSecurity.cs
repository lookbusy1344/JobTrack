namespace JobTrack.Database;

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
///     Deliberately duplicated from <c>JobTrack.Identity.PostgreSqlTransportSecurity</c> rather than
///     shared: this project has "no EF Core or domain dependency" by design (its own csproj
///     description), sitting below the reusable-library layer in the mandatory database → library →
///     HTTP API → web ordering, so it cannot reference <c>JobTrack.Identity</c>. Keep both copies'
///     logic identical if either changes.
/// </remarks>
public static class PostgreSqlTransportSecurity
{
	private static readonly FrozenSet<string> LoopbackHosts =
		FrozenSet.ToFrozenSet(["localhost", "127.0.0.1", "::1"], StringComparer.OrdinalIgnoreCase);

	/// <summary>
	///     Throws <see cref="SchemaDeploymentException" /> if <paramref name="connectionString" /> targets a
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
			throw new SchemaDeploymentException(
				$"PostgreSQL connection to remote host '{builder.Host}' must set 'SSL Mode=VerifyFull' " +
				$"with a trusted root certificate; found '{builder.SslMode}'. " +
				"'Trust Server Certificate=true' is not an acceptable substitute for a remote host.");
		}

		if (string.IsNullOrWhiteSpace(builder.RootCertificate)) {
			throw new SchemaDeploymentException(
				$"PostgreSQL connection to remote host '{builder.Host}' sets 'SSL Mode={builder.SslMode}' but no " +
				"'Root Certificate' -- a trusted CA file is required to authenticate the server.");
		}
	}

	private static bool IsLocal(string? host) =>
		string.IsNullOrEmpty(host) || host.StartsWith('/') || LoopbackHosts.Contains(host);
}
