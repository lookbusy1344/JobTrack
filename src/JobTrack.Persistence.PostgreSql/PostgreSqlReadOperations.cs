namespace JobTrack.Persistence.PostgreSql;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shared.Ports;

/// <summary>
///     The PostgreSQL read-path seam (ADR 0064), shared by every query port whose body lives in
///     <c>JobTrack.Persistence.Shared</c>. One <see cref="PostgreSqlJobTrackDbContext" /> per call.
/// </summary>
internal class PostgreSqlReadOperations(NpgsqlDataSource dataSource, IReadOnlyList<IInterceptor> interceptors)
	: IProviderReadOperations
{
	/// <summary>Creates the seam over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlReadOperations(NpgsqlDataSource dataSource)
		: this(dataSource, [])
	{
	}

	public DbContext CreateContext()
	{
		var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, provider => provider.UseNodaTime());
		if (interceptors.Count > 0) {
			optionsBuilder = optionsBuilder.AddInterceptors(interceptors);
		}

		return new PostgreSqlJobTrackDbContext(optionsBuilder.Options);
	}
}
