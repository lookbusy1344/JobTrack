namespace JobTrack.Persistence.PostgreSql;

using System.Text.Json;
using Abstractions;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IAuditQueryPort" /> (impl plan §7.3/§7.4 slice 11:
///     query audit history using sensitive-field projections). One
///     <see
///         cref="PostgreSqlJobTrackDbContext" />
///     per call, read-only throughout, matching every prior
///     read-only port's shape. Materializes every matching raw event unconditionally --
///     <see
///         cref="AuditQueries" />
///     applies <see cref="Domain.Authorization.AuditAccessPolicy" /> and the
///     per-event sensitive-field projection itself.
/// </summary>
internal sealed class PostgreSqlAuditQueryPort : IAuditQueryPort
{
	private readonly IClock clock;
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlAuditQueryPort(NpgsqlDataSource dataSource, IClock clock)
	{
		this.dataSource = dataSource;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		return await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<AuditSearchQueryResult> SearchAuditEventsAsync(
		AuditEventSearchFilter filter, AuditEventSearchCursor? before, int limit, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var events = await AuditQueryAssembly.SearchAsync(context, filter, before, limit, cancellationToken).ConfigureAwait(false);

		return new() { Events = EquatableArray.CopyOf(events) };
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, clock.GetCurrentInstant());

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}
}

/// <summary>
///     The audit-search assembly logic behind <see cref="PostgreSqlAuditQueryPort" />, mirrored
///     (necessarily duplicated, not literally shared) by SQLite's own <c>AuditQueryAssembly</c> --
///     see <c>CostQueryAssembly</c>'s own doc comment for why this cannot live in
///     <c>JobTrack.Persistence.Shared</c>.
/// </summary>
internal static class AuditQueryAssembly
{
	private static readonly string[] SensitiveEntityTypes = ["user_cost_rate", "node_rate_override"];

	public static async Task<List<AuditEventRecord>> SearchAsync(
		DbContext context, AuditEventSearchFilter filter, AuditEventSearchCursor? before, int limit, CancellationToken cancellationToken)
	{
		var query = context.Set<AuditEventEntity>().AsNoTracking().AsQueryable();

		if (filter.ActorId is AppUserId actorId) {
			query = query.Where(e => e.ActorUserId == actorId);
		}

		var type = filter.EntityType;
		if (type is not null) {
			query = query.Where(e => e.EntityType == type);
		}

		if (filter.EntityId is long entityId) {
			query = query.Where(e => e.EntityId == entityId);
		}

		if (filter.CorrelationId is Guid correlationId) {
			query = query.Where(e => e.CorrelationId == correlationId);
		}

		if (filter.From is Instant from) {
			query = query.Where(e => e.OccurredAt >= from);
		}

		if (filter.To is Instant to) {
			query = query.Where(e => e.OccurredAt < to);
		}

		if (before is not null) {
			query = query.Where(e =>
				e.OccurredAt < before.OccurredAt
				|| (e.OccurredAt == before.OccurredAt && e.Id < before.Id));
		}

		var rows = await query
			.OrderByDescending(e => e.OccurredAt)
			.ThenByDescending(e => e.Id)
			.Take(limit)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		return [.. rows.Select(ToRecord)];
	}

	private static AuditEventRecord ToRecord(AuditEventEntity entity) =>
		new() {
			Id = entity.Id,
			OccurredAt = entity.OccurredAt,
			ActorId = entity.ActorUserId,
			Operation = entity.Operation,
			EntityType = entity.EntityType,
			EntityId = entity.EntityId,
			CorrelationId = entity.CorrelationId,
			Reason = entity.Reason,
			BeforeData = ParseJson(entity.BeforeData),
			AfterData = ParseJson(entity.AfterData),
			IsSensitive = SensitiveEntityTypes.Contains(entity.EntityType),
		};

	private static EquatableDictionary<string, string?>? ParseJson(string? json) =>
		json is null ? null : EquatableDictionaryFactory.CopyOf(JsonSerializer.Deserialize<Dictionary<string, string?>>(json)!);
}
