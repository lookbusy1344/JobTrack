namespace JobTrack.Identity;

/// <summary>
///     Persistence shape of the <c>identity_user_role</c> join table (schema version 0002) as mapped
///     by <see cref="JobTrackIdentityDbContext" /> — a second, independent EF mapping of the same
///     physical table <c>JobTrack.Persistence.Shared</c>'s internal <c>IdentityUserRoleEntity</c> maps
///     for the library's own authorization checks (ADR 0022's established pattern: one mapping per
///     owning layer, not a shared reference).
/// </summary>
internal sealed class JobTrackIdentityUserRole
{
	public long IdentityUserId { get; set; }

	public short IdentityRoleId { get; set; }
}
