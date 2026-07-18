namespace JobTrack.AdminCli;

using System.Security.Cryptography;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     The <c>reset-password</c> command (plan §8.6; spec §7.1): a database-backed emergency mechanism
///     for when the normal web administration flow (the Administrator-only page shipped in the
///     disablement slice) cannot be used. Unlike <see cref="BootstrapCommand" />, this is new logic, not
///     a thin wrapper — it uses <c>JobTrack.Identity</c> and its configured
///     <see cref="IPasswordHasher{TUser}" /> directly (not the reusable library's employee-account
///     commands), matching the narrower <c>jobtrack_emergency_reset</c> PostgreSQL role this command is
///     meant to run under (<c>database/postgresql/roles/jobtrack-roles-and-grants.sql</c>): only
///     <c>SELECT</c>/<c>UPDATE</c> on <c>app_user</c>/<c>identity_user</c> and
///     <c>SELECT</c>/<c>INSERT</c> on <c>audit_event</c>.
/// </summary>
public static class EmergencyPasswordReset
{
	private const int TemporaryPasswordLength = 24;
	private const string TemporaryPasswordCharset = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*";
	private const string AuditOperation = "emergency-password-reset";
	private const string AuditEntityType = "identity_user";
	private const string AuditReason = "Operator-initiated emergency password reset via JobTrack.AdminCli.";

	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		JobTrackIdentityDbContext identityContext,
		IPasswordHasher<JobTrackIdentityUser> passwordHasher,
		AdminCliProvider provider,
		string username,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(identityContext);
		ArgumentNullException.ThrowIfNull(passwordHasher);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);

		var user = await userManager.FindByNameAsync(username).ConfigureAwait(false);
		if (user is null) {
			io.WriteError($"No employee account found for username '{username}'.");
			return 1;
		}

		var temporaryPassword = RandomNumberGenerator.GetString(TemporaryPasswordCharset, TemporaryPasswordLength);

		await using var transaction = await identityContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		user.PasswordHash = passwordHasher.HashPassword(user, temporaryPassword);
		user.SecurityStamp = Guid.NewGuid().ToString("N");
		user.RequiresPasswordChange = true;

		var updateResult = await userManager.UpdateAsync(user).ConfigureAwait(false);
		if (!updateResult.Succeeded) {
			io.WriteError($"Failed to update the account: {string.Join("; ", updateResult.Errors.Select(e => e.Description))}");
			return 1;
		}

		var correlationId = Guid.NewGuid();
		// No shared audit writer to reuse here (AuditEventWriter in JobTrack.Persistence.Shared is
		// internal to that assembly and mapped to a different DbContext) -- this is a new, narrow,
		// one-off insert, consistent with jobtrack_emergency_reset's grant being SELECT, INSERT
		// only on audit_event. actor_user_id records the target's own app_user_id: there is no
		// "system"/operator actor concept in the schema (actor_user_id is NOT NULL REFERENCES
		// app_user), so the operation/entity/reason combination is what makes this unambiguously
		// operator-initiated on review, not the actor column.
		//
		// occurred_at needs a provider-specific value: PostgreSQL's column defaults to now(), but
		// SQLite's has no default and stores ADR 0007's canonical encoding (signed 64-bit
		// Unix-epoch ticks), which this raw insert must supply explicitly since it bypasses EF's
		// entity mapping entirely.
		if (provider == AdminCliProvider.Sqlite) {
			var occurredAtTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"""
				 INSERT INTO audit_event (occurred_at, actor_user_id, operation, entity_type, entity_id, correlation_id, reason)
				 VALUES ({occurredAtTicks}, {user.AppUserId.Value}, {AuditOperation}, {AuditEntityType}, {user.Id}, {correlationId}, {AuditReason});
				 """,
				cancellationToken).ConfigureAwait(false);
			// ADR 0029: emergency password reset is the same credential-sensitivity class as an
			// administrator-driven reset -- it must revoke every live personal access token too.
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"UPDATE personal_access_token SET revoked_at = {occurredAtTicks} WHERE app_user_id = {user.AppUserId.Value} AND revoked_at IS NULL;",
				cancellationToken).ConfigureAwait(false);
		} else {
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"""
				 INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id, reason)
				 VALUES ({user.AppUserId.Value}, {AuditOperation}, {AuditEntityType}, {user.Id}, {correlationId}, {AuditReason});
				 """,
				cancellationToken).ConfigureAwait(false);
			_ = await identityContext.Database.ExecuteSqlInterpolatedAsync(
				$"UPDATE personal_access_token SET revoked_at = now() WHERE app_user_id = {user.AppUserId.Value} AND revoked_at IS NULL;",
				cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		io.WriteLine($"Temporary password for '{username}': {temporaryPassword}");
		io.WriteLine("Relay this credential to the employee out-of-band now -- it will not be shown again. " +
					 "The employee must change it at next sign-in, and any existing session has already been revoked.");
		return 0;
	}
}
