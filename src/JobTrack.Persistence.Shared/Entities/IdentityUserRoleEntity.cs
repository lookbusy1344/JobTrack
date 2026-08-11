namespace JobTrack.Persistence.Shared.Entities;

/// <summary>
///     Persistence shape of the <c>identity_user_role</c> join table (schema version 0002).
/// </summary>
internal sealed class IdentityUserRoleEntity
{
	public long IdentityUserId { get; set; }

	public short IdentityRoleId { get; set; }
}
