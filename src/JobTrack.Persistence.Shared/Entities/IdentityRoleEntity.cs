namespace JobTrack.Persistence.Shared.Entities;

/// <summary>
///     Persistence shape of the seeded <c>identity_role</c> reference table (schema version 0002).
/// </summary>
internal sealed class IdentityRoleEntity
{
	public short Id { get; set; }

	public required string Name { get; set; }
}
