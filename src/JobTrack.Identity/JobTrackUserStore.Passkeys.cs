namespace JobTrack.Identity;

using Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     <see cref="IUserPasskeyStore{TUser}" /> half of the hand-written store (ADR 0022, plan §7.2).
///     The framework's native attestation/assertion ceremonies read and write credentials here;
///     JobTrack's own enrol/remove/reset go through the atomic <c>IJobTrackClient</c> commands instead,
///     so this surface carries no stamp rotation, PAT revocation, or audit. Reads and writes hand back
///     copies of key material — no caller retains a reference into the tracked entity graph.
/// </summary>
public sealed partial class JobTrackUserStore
{
	private DbSet<JobTrackIdentityUserPasskey> Passkeys => dbContext.Set<JobTrackIdentityUserPasskey>();

	public async Task<JobTrackIdentityUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(credentialId);

		var ownerId = await Passkeys.AsNoTracking()
									.Where(passkey => passkey.CredentialId == credentialId)
									.Select(passkey => (long?)passkey.IdentityUserId)
									.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
		if (ownerId is null) {
			return null;
		}

		return await dbContext.Users.FirstOrDefaultAsync(user => user.Id == ownerId.Value, cancellationToken).ConfigureAwait(false);
	}

	public async Task<UserPasskeyInfo?> FindPasskeyAsync(JobTrackIdentityUser user, byte[] credentialId, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(user);
		ArgumentNullException.ThrowIfNull(credentialId);

		var passkey = await Passkeys.AsNoTracking()
									.FirstOrDefaultAsync(p => p.CredentialId == credentialId && p.IdentityUserId == user.Id, cancellationToken)
									.ConfigureAwait(false);
		return passkey is null ? null : ToPasskeyInfo(passkey);
	}

	public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(user);

		var passkeys = await Passkeys.AsNoTracking()
									 .Where(passkey => passkey.IdentityUserId == user.Id)
									 .OrderByDescending(passkey => passkey.CreatedAt)
									 .ThenBy(passkey => passkey.NormalizedName)
									 .ToListAsync(cancellationToken).ConfigureAwait(false);
		return passkeys.ConvertAll(ToPasskeyInfo);
	}

	public async Task AddOrUpdatePasskeyAsync(JobTrackIdentityUser user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(user);
		ArgumentNullException.ThrowIfNull(passkey);

		await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await LockCurrentUserAsync(user, cancellationToken).ConfigureAwait(false);
		var credentialId = passkey.CredentialId;
		var existing = await Passkeys.FirstOrDefaultAsync(p => p.CredentialId == credentialId, cancellationToken).ConfigureAwait(false);
		if (existing is not null) {
			UpdateExisting(existing, user, passkey);
		} else {
			_ = Passkeys.Add(ToNewEntity(user, passkey));
		}

		_ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task RemovePasskeyAsync(JobTrackIdentityUser user, byte[] credentialId, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(user);
		ArgumentNullException.ThrowIfNull(credentialId);

		var existing = await Passkeys.FirstOrDefaultAsync(p => p.CredentialId == credentialId && p.IdentityUserId == user.Id, cancellationToken)
									 .ConfigureAwait(false);
		if (existing is null) {
			return;
		}

		_ = Passkeys.Remove(existing);
		_ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	private static void UpdateExisting(JobTrackIdentityUserPasskey existing, JobTrackIdentityUser user, UserPasskeyInfo passkey)
	{
		// An assertion update touches only the mutable counter/backup state. Ownership, name, public
		// key, and AAGUID are fixed at enrolment; the signature counter never regresses below what is
		// stored (a rollback would indicate a cloned authenticator).
		if (existing.IdentityUserId != user.Id) {
			throw new InvalidOperationException("A passkey cannot be reassigned to a different account.");
		}

		var incomingSignCount = (long)passkey.SignCount;
		if (existing.SignCount > 0 && incomingSignCount <= existing.SignCount) {
			throw new InvalidOperationException("The passkey signature counter did not advance.");
		}

		if (incomingSignCount > existing.SignCount) {
			existing.SignCount = incomingSignCount;
		}

		existing.IsBackedUp = passkey.IsBackedUp;
		++existing.RowVersion;
	}

	private async Task LockCurrentUserAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		var affected = await dbContext.Users
									  .Where(candidate => candidate.Id == user.Id)
									  .ExecuteUpdateAsync(
										  setters => setters.SetProperty(candidate => candidate.ConcurrencyStamp, candidate => candidate.ConcurrencyStamp),
										  cancellationToken)
									  .ConfigureAwait(false);
		if (affected == 0) {
			throw new DbUpdateConcurrencyException("The passkey owner no longer exists.");
		}

		var persistedStamp = await dbContext.Users.AsNoTracking()
											.Where(candidate => candidate.Id == user.Id)
											.Select(candidate => candidate.ConcurrencyStamp)
											.SingleAsync(cancellationToken)
											.ConfigureAwait(false);
		if (!string.Equals(persistedStamp, user.ConcurrencyStamp, StringComparison.Ordinal)) {
			throw new DbUpdateConcurrencyException("The passkey owner changed while the assertion was being verified.");
		}
	}

	private static JobTrackIdentityUserPasskey ToNewEntity(JobTrackIdentityUser user, UserPasskeyInfo passkey)
	{
		if (!passkey.IsUserVerified) {
			throw new ArgumentException("A passkey without user verification cannot be stored (userVerification = required).", nameof(passkey));
		}

		var rawName = passkey.Name;
		if (rawName is null || !PasskeyPolicy.IsNameAcceptable(rawName)) {
			throw new ArgumentException("A passkey requires a friendly name of 1 to 100 code points.", nameof(passkey));
		}

		var name = rawName.Trim();
		return new() {
			CredentialId = CopyOf(passkey.CredentialId),
			IdentityUserId = user.Id,
			Name = name,
			NormalizedName = PasskeyPolicy.Normalize(name),
			PublicKey = CopyOf(passkey.PublicKey),
			CreatedAt = Instant.FromDateTimeOffset(passkey.CreatedAt),
			SignCount = passkey.SignCount,
			Transports = PasskeyTransports.Serialize(passkey.Transports),
			IsUserVerified = passkey.IsUserVerified,
			IsBackupEligible = passkey.IsBackupEligible,
			IsBackedUp = passkey.IsBackedUp,
			AttestationObject = CopyOf(passkey.AttestationObject),
			ClientDataJson = CopyOf(passkey.ClientDataJson),
			RowVersion = 1,
		};
	}

	private static UserPasskeyInfo ToPasskeyInfo(JobTrackIdentityUserPasskey passkey) =>
		new(
			CopyOf(passkey.CredentialId),
			CopyOf(passkey.PublicKey),
			passkey.CreatedAt.ToDateTimeOffset(),
			(uint)passkey.SignCount,
			PasskeyTransports.Parse(passkey.Transports),
			passkey.IsUserVerified,
			passkey.IsBackupEligible,
			passkey.IsBackedUp,
			CopyOf(passkey.AttestationObject),
			CopyOf(passkey.ClientDataJson)) {
			Name = passkey.Name,
		};

	private static byte[] CopyOf(byte[] source) => (byte[])source.Clone();
}
