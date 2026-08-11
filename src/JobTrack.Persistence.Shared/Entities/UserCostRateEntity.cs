namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>user_cost_rate</c> table (schema version 0011) — one effective-dated
///     hourly cost rate for a user.
/// </summary>
internal sealed class UserCostRateEntity
{
	public required UserCostRateId Id { get; set; }

	public required AppUserId UserId { get; set; }

	public Instant EffectiveStart { get; set; }

	public Instant? EffectiveEnd { get; set; }

	public HourlyRate Rate { get; set; }

	public Instant ChangedAt { get; set; }

	public long RowVersion { get; set; }
}
