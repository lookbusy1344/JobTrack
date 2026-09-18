namespace JobTrack.Web;

using Identity;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     Holds one startup-created dummy password hash so username login paths which Identity rejects
///     before password verification still perform one current-policy verification. The dummy user
///     is never persisted and no guessed account's failure state is changed.
/// </summary>
public sealed class LoginPasswordWorkEqualizer
{
	private const string DummyUserName = "login-work-equalizer";
	private readonly string dummyHash;

	private readonly JobTrackIdentityUser dummyUser = new() {
		AppUserId = new(long.MaxValue),
		UserName = DummyUserName,
		NormalizedUserName = DummyUserName.ToUpperInvariant(),
		PasswordHash = string.Empty,
		SecurityStamp = DummyUserName,
		ConcurrencyStamp = DummyUserName,
	};

	public LoginPasswordWorkEqualizer(IServiceScopeFactory scopeFactory)
	{
		using var scope = scopeFactory.CreateScope();
		var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<JobTrackIdentityUser>>();
		dummyHash = passwordHasher.HashPassword(dummyUser, DummyUserName);
	}

	public void Verify(IPasswordHasher<JobTrackIdentityUser> passwordHasher, string password)
	{
		ArgumentNullException.ThrowIfNull(passwordHasher);
		ArgumentNullException.ThrowIfNull(password);
		_ = passwordHasher.VerifyHashedPassword(dummyUser, dummyHash, password);
	}
}
