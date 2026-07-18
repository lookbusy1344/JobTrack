namespace JobTrack.Identity;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     PostgreSQL <see cref="JobTrackIdentityDbContext" />: <c>lockout_end</c> maps natively to
///     <c>timestamptz</c> via Npgsql's built-in <see cref="DateTimeOffset" /> support — no manual
///     conversion (contrast <see cref="SqliteJobTrackIdentityDbContext" />).
/// </summary>
public sealed class PostgreSqlJobTrackIdentityDbContext : JobTrackIdentityDbContext
{
	public PostgreSqlJobTrackIdentityDbContext(DbContextOptions<PostgreSqlJobTrackIdentityDbContext> options)
		: base(options)
	{
	}
}
