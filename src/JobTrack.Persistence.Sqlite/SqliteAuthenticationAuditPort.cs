namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;

internal sealed class SqliteAuthenticationAuditPort : IAuthenticationAuditPort
{
	private const string KnownEntityType = "identity_user";
	private const string UnknownEntityType = "authentication_attempt";

	/// <summary>
	///     <c>entity_id</c> is a NOT NULL column with no unknown-subject case, unlike <c>actor_user_id</c>
	///     -- an unknown-username failure names no real <c>identity_user</c> row, so this sentinel (below
	///     every real generated id) stands in rather than falling back to an actor id that no longer
	///     exists for this event (fresh-eyes review §2.6).
	/// </summary>
	private const long UnknownSubjectEntityId = 0;

	private readonly IClock clock;

	private readonly string connectionString;

	public SqliteAuthenticationAuditPort(string connectionString, IClock clock)
	{
		this.connectionString = connectionString;
		this.clock = clock;
	}

	public async Task RecordAsync(RecordAuthenticationAuditEventRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var entityType = request.IdentityUserId is null
			? UnknownEntityType
			: KnownEntityType;
		var entityId = request.IdentityUserId ?? UnknownSubjectEntityId;
		var afterData = request.IdentityUserId is null
			? new Dictionary<string, string?> { ["subject"] = "redacted" }
			: null;

		AuditEventWriter.Add(
			context,
			request.ActorUserId,
			clock.GetCurrentInstant(),
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
