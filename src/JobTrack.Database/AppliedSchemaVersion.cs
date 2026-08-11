namespace JobTrack.Database;

/// <summary>
///     One row recorded in the <c>schema_version</c> tracking table: which script
///     applied, its checksum at the time, and who/what/when applied it (ADR 0011).
/// </summary>
public sealed record AppliedSchemaVersion
{
	public required int Version { get; init; }

	public required string Description { get; init; }

	public required string Checksum { get; init; }

	public required string ApplicationVersion { get; init; }

	public required string AppliedBy { get; init; }

	public required DateTimeOffset AppliedAtUtc { get; init; }
}
