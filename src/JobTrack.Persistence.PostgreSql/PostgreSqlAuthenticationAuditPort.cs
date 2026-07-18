namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

internal sealed class PostgreSqlAuthenticationAuditPort : IAuthenticationAuditPort
{
	private const string KnownEntityType = "identity_user";
	private const string UnknownEntityType = "authentication_attempt";
	private const string SystemActorDisplayName = "JobTrack authentication audit";
	private const string SystemActorTimeZone = "UTC";

	private readonly NpgsqlDataSource dataSource;

	public PostgreSqlAuthenticationAuditPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	public async Task RecordAsync(RecordAuthenticationAuditEventRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var actorUserId = request.ActorUserId ?? await GetOrCreateSystemActorAsync(context, cancellationToken).ConfigureAwait(false);
		var entityType = request.IdentityUserId is null
			? UnknownEntityType
			: KnownEntityType;
		var entityId = request.IdentityUserId ?? actorUserId.Value;
		var afterData = request.IdentityUserId is null
			? new Dictionary<string, string?> { ["subject"] = "redacted" }
			: null;

		AuditEventWriter.Add(
			context,
			actorUserId,
			SystemClock.Instance.GetCurrentInstant(),
			OperationFor(request.Kind),
			entityType,
			entityId,
			request.CorrelationId,
			null,
			null,
			afterData);

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static async Task<AppUserId> GetOrCreateSystemActorAsync(
		PostgreSqlJobTrackDbContext context, CancellationToken cancellationToken)
	{
		var existing = await context.Set<AppUserEntity>().AsNoTracking()
			.Where(user => user.DisplayName == SystemActorDisplayName)
			.Select(user => user.Id)
			.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
		if (!existing.IsUnspecified) {
			return existing;
		}

		var systemActor = new AppUserEntity {
			Id = default,
			DisplayName = SystemActorDisplayName,
			IanaTimeZone = SystemActorTimeZone,
			RowVersion = 1,
		};
		_ = context.Add(systemActor);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return systemActor.Id;
	}

	private static string OperationFor(AuthenticationAuditEventKind kind) =>
		kind switch {
			AuthenticationAuditEventKind.LoginSuccess => "authentication.login-success",
			AuthenticationAuditEventKind.LoginFailed => "authentication.login-failed",
			AuthenticationAuditEventKind.Lockout => "authentication.lockout",
			AuthenticationAuditEventKind.Logout => "authentication.logout",
			AuthenticationAuditEventKind.PasswordChanged => "authentication.password-change",
			AuthenticationAuditEventKind.TwoFactorEnabled => "authentication.two-factor-enabled",
			AuthenticationAuditEventKind.TwoFactorDisabled => "authentication.two-factor-disabled",
			AuthenticationAuditEventKind.TwoFactorFailed => "authentication.two-factor-failed",
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown authentication audit event kind."),
		};
}
