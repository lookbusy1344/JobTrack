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
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlAuditQueryPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		return await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<AuditSearchQueryResult> SearchAuditEventsAsync(
		AppUserId actorId, AuditEventSearchFilter filter, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
		var events = await AuditQueryAssembly.SearchAsync(context, filter, cancellationToken).ConfigureAwait(false);

		return new() { ActorRoles = actorRoles, Events = EquatableArray.CopyOf(events) };
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, SystemClock.Instance.GetCurrentInstant());

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
		DbContext context, AuditEventSearchFilter filter, CancellationToken cancellationToken)
	{
		var query = context.Set<AuditEventEntity>().AsNoTracking().AsQueryable();

		if (filter.ActorId is { } actorId) {
			query = query.Where(e => e.ActorUserId == actorId);
		}

		if (filter.EntityType is { } entityType) {
			query = query.Where(e => e.EntityType == entityType);
		}

		if (filter.EntityId is { } entityId) {
			query = query.Where(e => e.EntityId == entityId);
		}

		if (filter.CorrelationId is { } correlationId) {
			query = query.Where(e => e.CorrelationId == correlationId);
		}

		if (filter.From is { } from) {
			query = query.Where(e => e.OccurredAt >= from);
		}

		if (filter.To is { } to) {
			query = query.Where(e => e.OccurredAt < to);
		}

		var rows = await query
			.OrderByDescending(e => e.OccurredAt)
			.ThenByDescending(e => e.Id)
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
