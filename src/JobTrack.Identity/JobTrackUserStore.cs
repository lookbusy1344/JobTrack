namespace JobTrack.Identity;

using System.Globalization;
using System.Text;
using Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Hand-written EF-backed <see cref="JobTrackIdentityUser" /> store (ADR 0022): identity, password,
///     security-stamp, lockout, role membership, and optional TOTP two-factor authentication (ADR
///     0037). Role membership (§8.3) matches role names case-insensitively against the six seeded
///     <c>identity_role</c> rows rather than relying on <see cref="UserManager{TUser}" />'s
///     upper-invariant name normalization matching this project's mixed-case, space-containing
///     canonical names ("Job manager", not "JOB MANAGER").
/// </summary>
public sealed class JobTrackUserStore :
	IUserStore<JobTrackIdentityUser>,
	IUserPasswordStore<JobTrackIdentityUser>,
	IUserSecurityStampStore<JobTrackIdentityUser>,
	IUserLockoutStore<JobTrackIdentityUser>,
	IUserRoleStore<JobTrackIdentityUser>,
	IUserTwoFactorStore<JobTrackIdentityUser>,
	IUserAuthenticatorKeyStore<JobTrackIdentityUser>
{
	/// <summary>
	///     Data Protection purpose string scoping the key used to encrypt/decrypt
	///     <see cref="JobTrackIdentityUser.AuthenticatorKeyProtected" /> (ADR 0037) -- distinct from any
	///     other purpose this host's key ring might later serve.
	/// </summary>
	private const string AuthenticatorKeyProtectionPurpose = "JobTrack.Identity.AuthenticatorKey.v1";

	private readonly IDataProtector authenticatorKeyProtector;

	private readonly IClock clock;

	private readonly JobTrackIdentityDbContext dbContext;

	public JobTrackUserStore(JobTrackIdentityDbContext dbContext, IDataProtectionProvider dataProtectionProvider, IClock clock)
	{
		this.dbContext = dbContext;
		this.clock = clock;
		authenticatorKeyProtector = dataProtectionProvider.CreateProtector(AuthenticatorKeyProtectionPurpose);
	}

	public Task SetAuthenticatorKeyAsync(JobTrackIdentityUser user, string key, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(key);
		user.AuthenticatorKeyProtected = authenticatorKeyProtector.Protect(Encoding.UTF8.GetBytes(key));
		return Task.CompletedTask;
	}

	public Task<string?> GetAuthenticatorKeyAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult(user.AuthenticatorKeyProtected is null
			? null
			: Encoding.UTF8.GetString(authenticatorKeyProtector.Unprotect(user.AuthenticatorKeyProtected)));

	public Task<DateTimeOffset?> GetLockoutEndDateAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult(user.LockoutEnd);

	public Task SetLockoutEndDateAsync(JobTrackIdentityUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
	{
		user.LockoutEnd = lockoutEnd;
		return Task.CompletedTask;
	}

	public Task<int> IncrementAccessFailedCountAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		user.AccessFailedCount++;
		return Task.FromResult(user.AccessFailedCount);
	}

	public Task ResetAccessFailedCountAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		user.AccessFailedCount = 0;
		return Task.CompletedTask;
	}

	public Task<int> GetAccessFailedCountAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult(user.AccessFailedCount);

	public Task<bool> GetLockoutEnabledAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult(user.LockoutEnabled);

	public Task SetLockoutEnabledAsync(JobTrackIdentityUser user, bool enabled, CancellationToken cancellationToken)
	{
		user.LockoutEnabled = enabled;
		return Task.CompletedTask;
	}

	public Task SetPasswordHashAsync(JobTrackIdentityUser user, string? passwordHash, CancellationToken cancellationToken)
	{
		user.PasswordHash = passwordHash ?? throw new ArgumentNullException(nameof(passwordHash));
		return Task.CompletedTask;
	}

	public Task<string?> GetPasswordHashAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult<string?>(user.PasswordHash);

	public Task<bool> HasPasswordAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult(true);

	public async Task AddToRoleAsync(JobTrackIdentityUser user, string roleName, CancellationToken cancellationToken)
	{
		var roleId = await FindRoleIdAsync(roleName, cancellationToken).ConfigureAwait(false)
					 ?? throw new ArgumentException($"Role '{roleName}' does not exist.", nameof(roleName));

		var alreadyAssigned = await dbContext.Set<JobTrackIdentityUserRole>().AsNoTracking()
			.AnyAsync(ur => ur.IdentityUserId == user.Id && ur.IdentityRoleId == roleId, cancellationToken).ConfigureAwait(false);
		if (alreadyAssigned) {
			return;
		}

		_ = dbContext.Add(new JobTrackIdentityUserRole { IdentityUserId = user.Id, IdentityRoleId = roleId });
		_ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task RemoveFromRoleAsync(JobTrackIdentityUser user, string roleName, CancellationToken cancellationToken)
	{
		var roleId = await FindRoleIdAsync(roleName, cancellationToken).ConfigureAwait(false);
		if (roleId is null) {
			return;
		}

		var existing = await dbContext.Set<JobTrackIdentityUserRole>()
			.FirstOrDefaultAsync(ur => ur.IdentityUserId == user.Id && ur.IdentityRoleId == roleId, cancellationToken).ConfigureAwait(false);
		if (existing is null) {
			return;
		}

		_ = dbContext.Remove(existing);
		_ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IList<string>> GetRolesAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		var roleIds = await dbContext.Set<JobTrackIdentityUserRole>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == user.Id)
			.Select(ur => ur.IdentityRoleId)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var roles = await dbContext.Set<JobTrackIdentityRole>().AsNoTracking()
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Name)];
	}

	public async Task<bool> IsInRoleAsync(JobTrackIdentityUser user, string roleName, CancellationToken cancellationToken)
	{
		var roleId = await FindRoleIdAsync(roleName, cancellationToken).ConfigureAwait(false);
		if (roleId is null) {
			return false;
		}

		return await dbContext.Set<JobTrackIdentityUserRole>().AsNoTracking()
			.AnyAsync(ur => ur.IdentityUserId == user.Id && ur.IdentityRoleId == roleId, cancellationToken).ConfigureAwait(false);
	}

	public async Task<IList<JobTrackIdentityUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
	{
		var roleId = await FindRoleIdAsync(roleName, cancellationToken).ConfigureAwait(false);
		if (roleId is null) {
			return [];
		}

		var userIds = await dbContext.Set<JobTrackIdentityUserRole>().AsNoTracking()
			.Where(ur => ur.IdentityRoleId == roleId)
			.Select(ur => ur.IdentityUserId)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		return await dbContext.Users.AsNoTracking()
			.Where(u => userIds.Contains(u.Id))
			.ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task SetSecurityStampAsync(JobTrackIdentityUser user, string stamp, CancellationToken cancellationToken)
	{
		user.SecurityStamp = stamp;
		return Task.CompletedTask;
	}

	public Task<string?> GetSecurityStampAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult<string?>(user.SecurityStamp);

	public void Dispose()
	{
	}

	public Task<string> GetUserIdAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult(user.Id.ToString(CultureInfo.InvariantCulture));

	public Task<string?> GetUserNameAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult<string?>(user.UserName);

	public Task SetUserNameAsync(JobTrackIdentityUser user, string? userName, CancellationToken cancellationToken)
	{
		user.UserName = userName ?? throw new ArgumentNullException(nameof(userName));
		return Task.CompletedTask;
	}

	public Task<string?> GetNormalizedUserNameAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult<string?>(user.NormalizedUserName);

	public Task SetNormalizedUserNameAsync(JobTrackIdentityUser user, string? normalizedName, CancellationToken cancellationToken)
	{
		user.NormalizedUserName = normalizedName ?? throw new ArgumentNullException(nameof(normalizedName));
		return Task.CompletedTask;
	}

	public async Task<IdentityResult> CreateAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		_ = dbContext.Users.Add(user);
		_ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return IdentityResult.Success;
	}

	public async Task<IdentityResult> UpdateAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		// Marking Modified first captures the as-loaded ConcurrencyStamp as EF's original value for
		// the optimistic-concurrency check; regenerating it afterwards only changes the current
		// value, so SaveChanges compares against what was actually read, not the new one.
		dbContext.Entry(user).State = EntityState.Modified;
		user.ConcurrencyStamp = Guid.NewGuid().ToString();
		_ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return IdentityResult.Success;
	}

	public async Task<IdentityResult> DeleteAsync(JobTrackIdentityUser user, CancellationToken cancellationToken)
	{
		_ = dbContext.Users.Remove(user);
		_ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return IdentityResult.Success;
	}

	public Task<JobTrackIdentityUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
	{
		var id = long.Parse(userId, CultureInfo.InvariantCulture);
		return dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
	}

	public Task<JobTrackIdentityUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
		dbContext.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);

	public Task SetTwoFactorEnabledAsync(JobTrackIdentityUser user, bool enabled, CancellationToken cancellationToken)
	{
		user.TwoFactorEnabled = enabled;
		user.TwoFactorEnabledAt = enabled ? clock.GetCurrentInstant().ToDateTimeOffset() : null;
		return Task.CompletedTask;
	}

	public Task<bool> GetTwoFactorEnabledAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		Task.FromResult(user.TwoFactorEnabled);

	/// <summary>
	///     Overwrites <paramref name="user" />'s current and original tracked values with what is now
	///     actually committed in the database. Unlike re-querying by id, which EF Core's identity map
	///     resolves against the already-tracked instance without touching its (possibly locally mutated,
	///     never-persisted) property values, this forces a genuine round trip -- the correct way to
	///     recover after this same <paramref name="user" /> lost an optimistic-concurrency race in
	///     <see cref="UpdateAsync" /> and the caller needs to see what the winning write actually
	///     persisted.
	/// </summary>
	public Task ReloadAsync(JobTrackIdentityUser user, CancellationToken cancellationToken) =>
		dbContext.Entry(user).ReloadAsync(cancellationToken);

	/// <summary>
	///     Looks up the credential row by its <c>app_user</c> identifier rather than its own store key.
	///     Used by the bearer authentication scheme (ADR 0029) to build the same claims principal the
	///     cookie scheme produces once a personal access token has resolved to an <see cref="AppUserId" />.
	/// </summary>
	public Task<JobTrackIdentityUser?> FindByAppUserIdAsync(AppUserId appUserId, CancellationToken cancellationToken) =>
		dbContext.Users.FirstOrDefaultAsync(u => u.AppUserId == appUserId, cancellationToken);

	/// <summary>
	///     Matches <paramref name="roleName" /> case-insensitively against the seeded
	///     <c>identity_role</c> rows — see the class summary for why an ordinal-ignore-case in-memory
	///     match is used instead of a database-side case-fold.
	/// </summary>
	private async Task<short?> FindRoleIdAsync(string roleName, CancellationToken cancellationToken)
	{
		var roles = await dbContext.Set<JobTrackIdentityRole>().AsNoTracking()
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		return roles.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase))?.Id;
	}
}
