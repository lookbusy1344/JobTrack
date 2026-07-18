namespace JobTrack.Database;

/// <summary>
///     One forward-only, source-controlled schema-version script loaded from
///     <c>database/{provider}/schema-versions/NNNN_description.sql</c> (ADR 0011).
/// </summary>
public sealed record SchemaVersionScript
{
	public required int Version { get; init; }

	public required string Description { get; init; }

	public required string FilePath { get; init; }

	public required string Sql { get; init; }

	public required string Checksum { get; init; }
}
