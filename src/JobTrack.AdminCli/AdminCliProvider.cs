namespace JobTrack.AdminCli;

/// <summary>
///     The database provider a CLI command targets — mirrors <c>JobTrack.Database</c>'s
///     <c>SchemaProvider</c> enum and <c>--provider</c> flag values, kept local rather than adding a
///     project reference to <c>JobTrack.Database</c> for one enum.
/// </summary>
public enum AdminCliProvider
{
	PostgreSql,
	Sqlite,
}
