namespace JobTrack.Application;

using NodaTime;

/// <summary>Shared account-level failure ceiling for every reusable authentication credential.</summary>
public static class AccountLockoutPolicy
{
	/// <summary>Number of consecutive reusable-credential failures which locks an enabled account.</summary>
	public const int MaxFailedAccessAttempts = 5;

	/// <summary>Duration of the temporary account lockout after the failure ceiling is reached.</summary>
	public static Duration LockoutDuration => Duration.FromMinutes(15);
}
