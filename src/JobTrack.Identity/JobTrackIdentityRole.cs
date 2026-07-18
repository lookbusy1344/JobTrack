namespace JobTrack.Identity;

/// <summary>
///     Persistence shape of the fixed, six-row <c>identity_role</c> reference table (schema version
///     0002) as mapped by <see cref="JobTrackIdentityDbContext" /> — read-only from this project's
///     perspective; the rows themselves are seeded by the schema deployment scripts, never written
///     here.
/// </summary>
internal sealed class JobTrackIdentityRole
{
	public short Id { get; set; }

	public required string Name { get; set; }
}
