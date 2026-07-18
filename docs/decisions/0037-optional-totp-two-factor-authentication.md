# ADR 0037: Optional TOTP two-factor authentication, self-service enrolment, admin/CLI reset

**Status:** Accepted
**Amends:** `jobtrack_spec_codex.md` §7.1 ("MFA is not required in the initial release... shall not
expose a non-functional enrolment flow"); `jobtrack_spec_claude.md` §6.2 (same). Extends ADR 0022's
`JobTrack.Identity` store surface.

## Context

§7.1 scoped MFA out of v1 but required the design to "reserve a clean extension point for passkeys"
and forbade exposing a non-functional enrolment flow in the meantime. That reservation is now being
exercised: this ADR adds optional TOTP-based two-factor authentication as a real, functional
feature, not a placeholder. Per this repository's decision-precedence convention, extending a
spec-scoped-out capability is surfaced here rather than assumed.

Requirements driving this decision:

1. Two-factor authentication is opt-in per employee, never mandatory.
2. Enrolment is self-service: an employee scans a QR code with a standard authenticator app (Google
   Authenticator, Microsoft Authenticator, Authy, etc.) and confirms with a generated code.
3. An employee can also disable it themselves.
4. An administrator can reset (disable) two-factor for another employee's account, through the
   existing Administrator-only account-management surface.
5. `JobTrack.AdminCli` can reset two-factor for any account, including the bootstrap administrator
   account itself, for when the web flow can't be used — mirroring the existing emergency
   `reset-password` command's role.
6. An employee who has lost their authenticator device and can no longer produce a code is not
   permanently locked out: an administrator or the CLI can clear two-factor on their account,
   letting them sign in with password alone and re-enrol if they choose. This is the *only* new
   "locked out" scenario in scope — it is unrelated to, and does not change, the existing
   password-attempt lockout (`MaxFailedAccessAttempts = 5`, 5-minute auto-expiry), which already
   self-heals without any admin/CLI action today.

## Decision

### Storage

`JobTrack.Identity`'s hand-rolled store (ADR 0022) gains two-factor support directly — no move to
the generic EF Identity store is warranted; this is "more interfaces on the same type," the same
extension shape ADR 0022 anticipated for roles.

Schema version 0002 (`database/{postgresql,sqlite}/schema-versions/0002_app-user-and-identity-storage.sql`)
is edited in place (pre-release, ADR 0011 not yet live) to add to `identity_user`:

- `two_factor_enabled` (`boolean not null default false`)
- `authenticator_key_protected` (`bytea` / `blob`, nullable) — the TOTP shared secret, encrypted at
  rest via ASP.NET Core Data Protection (`IDataProtector`), never stored or logged in plaintext. This
  is a "reusable secret" under §7.1's authentication baseline, so it gets the same never-plaintext
  treatment as password hashes and reset secrets, via encryption rather than hashing because the
  server must recover the secret to compute/verify codes.
- `two_factor_enabled_at` (`timestamptz` / `text`, nullable) — audit/support field only.

`JobTrackUserStore` implements `IUserTwoFactorStore<JobTrackIdentityUser>` and
`IUserAuthenticatorKeyStore<JobTrackIdentityUser>` against these columns. No recovery-code store
(`IUserTwoFactorRecoveryCodeStore`) is implemented — see "No recovery codes" below.

### Login flow

`SignInManager<JobTrackIdentityUser>`'s built-in two-factor flow is used as-is rather than hand-rolled:
`PasswordSignInAsync` already returns `RequiresTwoFactor` once `IUserTwoFactorStore` reports the flag
set, and `TwoFactorAuthenticatorSignInAsync` verifies the submitted code through the standard
`AuthenticatorTokenProvider<TUser>` (registered via `TokenOptions.DefaultAuthenticatorProvider`).
`Pages/Account/Login.cshtml.cs` gains a redirect to a new `Pages/Account/LoginTwoFactor.cshtml`
challenge step on `RequiresTwoFactor`, matching the framework's documented pattern. Failed codes
count against the existing lockout policy — no new brute-force control is introduced because the
existing one already applies.

### Self-service enrolment/disable

A new `Pages/Account/ManageTwoFactor.cshtml` page (available to any signed-in employee, no special
role):

- **Enrol:** generate/reset the authenticator key (`UserManager.ResetAuthenticatorKeyAsync`), render
  it as an `otpauth://totp/...` URI (label = username, issuer = `JobTrack`) turned into a QR image
  server-side, and require the employee to submit one valid code
  (`UserManager.VerifyTwoFactorTokenAsync`) before `SetTwoFactorEnabledAsync(user, true)` — the code
  proves the app was actually configured correctly, satisfying §7.1's "no non-functional enrolment
  flow" bar.
- **Disable:** require the employee's current password, then
  `SetTwoFactorEnabledAsync(user, false)` and clear the stored key.

### Admin reset

`IEmployeeCommands` gains `ResetTwoFactorAsync`, gated by the same `EmployeeAccessPolicy.CanManageAccounts`
policy as `ResetPasswordAsync`/`SetEnabledAsync` (ADR 0023's pattern). It clears
`two_factor_enabled`/`authenticator_key_protected` and revokes existing sessions (security-stamp
bump), audited the same way as the other account-management operations (actor, target, result, no
secrets). `Pages/Admin/ManageEmployeeAccount.cshtml.cs` gains an `OnPostResetTwoFactorAsync` handler
calling it, alongside the existing four handlers.

### CLI reset

`JobTrack.AdminCli` gains a `reset-2fa <username>` command following `EmergencyPasswordReset.cs`'s
existing shape: direct `UserManager<JobTrackIdentityUser>` access under the narrow-privilege
`jobtrack_emergency_reset` DB role, clearing the same two columns and writing the same audit-log
insert pattern as the emergency password reset. This works for any account, including the bootstrap
administrator, since it never depends on being able to authenticate first.

### No recovery codes

ASP.NET Core Identity's default recovery-code mechanism is deliberately not implemented. The
"lost device" scenario in requirement 6 is already covered by admin/CLI reset, which this project
already has as its sole account-recovery mechanism for lost passwords (spec §7.2: "there is no
automated end-user account recovery. An employee who loses access contacts an administrator.").
Adding self-service recovery codes would be a second, redundant recovery path with its own storage
and secrecy obligations, inconsistent with that existing policy.

### New third-party dependencies

Two focused NuGet packages are needed and are called out explicitly since they're new external
dependencies: a TOTP code library (e.g. `OtpNet`) for RFC 6238 generation/verification feeding the
custom `AuthenticatorTokenProvider` wiring, and a QR-image-rendering library (e.g. `QRCoder`) for the
enrolment page — both offline, no network calls, MIT-licensed. If the maintainers prefer different
packages, that's an implementation-time swap, not a decision this ADR is pinned to.

## Consequences

- `database/{postgresql,sqlite}/schema-versions/0002_...sql`, both `JobTrackIdentityDbContext`
  subclasses, and `JobTrackUserStore` all change together, per ADR 0022's stated maintenance cost of
  keeping the hand-rolled store in sync with the schema.
- `Program.cs` registers `AddTokenProvider<AuthenticatorTokenProvider<JobTrackIdentityUser>>` and the
  Data Protection services needed to protect/unprotect the authenticator key.
- `IEmployeeCommands`, `Pages/Account/{Login,LoginTwoFactor,ManageTwoFactor}.cshtml(.cs)`,
  `Pages/Admin/ManageEmployeeAccount.cshtml(.cs)`, and `JobTrack.AdminCli` all gain new
  surface — implementation follows the mandatory bottom-up order (schema → library → CLI/web).
- `jobtrack_spec_codex.md` §7.1 and `jobtrack_spec_claude.md` §6.2 need a follow-up wording update
  once this ADR is accepted, replacing "MFA is not required... shall not expose a non-functional
  enrolment flow" with a description of the shipped optional-TOTP behaviour — tracked as part of
  landing this ADR, not left dangling.
- Audit logging gains new event kinds (2FA enabled, disabled, admin-reset, CLI-reset) alongside the
  existing password/lockout/permission audit events, per §7.1's blanket audit requirement.
