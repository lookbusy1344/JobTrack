namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;

/// <summary>
///     Persistence shape of the <c>app_user</c> table (schema version 0002) — the employee-domain
///     profile, with no credential data. A mutable EF entity, never the domain/application model.
/// </summary>
internal sealed class AppUserEntity
{
	public required AppUserId Id { get; set; }

	public required string DisplayName { get; set; }

	public required string IanaTimeZone { get; set; }

	public HourlyRate? DefaultHourlyRate { get; set; }

	/// <summary>
	///     The node this employee lands on after login instead of the tree root (schema
	///     version 0004's <c>ALTER TABLE app_user ADD COLUMN home_node_id</c>) -- a navigation
	///     convenience with no ownership/authorization weight.
	/// </summary>
	public JobNodeId? HomeNodeId { get; set; }

	public long RowVersion { get; set; }
}
