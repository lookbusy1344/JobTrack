namespace JobTrack.Persistence.Shared.Converters;

using Abstractions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
///     Strongly typed identifier converters shared by both providers (ADR 0006): every identifier is
///     a <c>bigint</c>/<c>INTEGER</c> column on both PostgreSQL and SQLite, so the conversion itself
///     never diverges by provider.
/// </summary>
internal static class IdValueConverters
{
	public static readonly ValueConverter<AppUserId, long> AppUserId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<AppUserId?, long?> NullableAppUserId =
		new(id => id == null ? null : id.Value.Value, value => value == null ? null : new AppUserId(value.Value));

	public static readonly ValueConverter<JobNodeId, long> JobNodeId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<JobNodeId?, long?> NullableJobNodeId =
		new(id => id == null ? null : id.Value.Value, value => value == null ? null : new JobNodeId(value.Value));

	public static readonly ValueConverter<WorkSessionId, long> WorkSessionId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<ScheduleVersionId, long> ScheduleVersionId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<ScheduleExceptionId, long> ScheduleExceptionId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<UserCostRateId, long> UserCostRateId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<NodeRateOverrideId, long> NodeRateOverrideId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<AuditEventId, long> AuditEventId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<PersonalAccessTokenId, long> PersonalAccessTokenId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<DepartmentId, long> DepartmentId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<DepartmentId?, long?> NullableDepartmentId =
		new(id => id == null ? null : id.Value.Value, value => value == null ? null : new DepartmentId(value.Value));

	public static readonly ValueConverter<RequestHoldingAreaId, long> RequestHoldingAreaId =
		new(id => id.Value, value => new(value));

	public static readonly ValueConverter<JobRequestNoteId, long> JobRequestNoteId =
		new(id => id.Value, value => new(value));
}
