namespace JobTrack.Application;

/// <summary>Authentication and credential self-service events recorded in the append-only audit trail.</summary>
public enum AuthenticationAuditEventKind
{
	/// <summary>A password or completed two-factor sign-in succeeded.</summary>
	LoginSuccess,

	/// <summary>A password sign-in failed.</summary>
	LoginFailed,

	/// <summary>An account was locked by repeated authentication failures.</summary>
	Lockout,

	/// <summary>A signed-in user logged out.</summary>
	Logout,

	/// <summary>A signed-in user changed their own password.</summary>
	PasswordChanged,

	/// <summary>A signed-in user enabled two-factor authentication.</summary>
	TwoFactorEnabled,

	/// <summary>A signed-in user disabled two-factor authentication.</summary>
	TwoFactorDisabled,

	/// <summary>A two-factor challenge failed.</summary>
	TwoFactorFailed,

	/// <summary>A passkey (WebAuthn) sign-in succeeded (ADR 0071 §9). No credential material is recorded.</summary>
	PasskeySignInSuccess,

	/// <summary>A passkey (WebAuthn) sign-in attempt failed (ADR 0071 §9). Recorded without a known actor for a username-less assertion.</summary>
	PasskeySignInFailed,
}
