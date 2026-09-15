# ADR 0071: Passkeys as an optional primary sign-in method

**Status:** Accepted
**Date:** 2026-09-13
**Supersedes in part:** spec §7.1's "reserved extension point" clause for passkeys.
**Implements:** `docs/plans/2026-09-01-passkey-primary-sign-in-plan.md`.
**Relates to:** ADR 0022 (hand-written Identity store), ADR 0057 (step-up and absolute session
ceiling), ADR 0066 (multi-instance Cloud Run topology and shared Data Protection key ring).

## Context

Spec §7.1 reserved a "clean extension point for passkeys" but forbade a functional enrolment flow.
.NET 10 / ASP.NET Core Identity 10 ships native WebAuthn passkey support — option generation,
protected ceremony state, attestation/assertion validation, `UserPasskeyInfo`,
`IUserPasskeyStore<TUser>`, and `UserManager`/`SignInManager` passkey APIs — so a first-party passkey
sign-in no longer requires a third-party WebAuthn library or hand-written protocol code. This ADR
turns the reserved extension point into a live, optional primary sign-in method and fixes the
decisions the plan defers to an accepted record.

## Decision

### 1. Optional primary credential, no passkey-only account

Passkeys are an optional primary sign-in method alongside a retained password. Every account keeps a
password; JobTrack introduces no passkey-only accounts, no public registration, and no automated
account recovery. Password fallback plus administrator / Admin CLI reset remains the whole recovery
model. Disabling the feature hides enrolment and sign-in but retains stored credentials and leaves
password and TOTP working.

### 2. Three authentication paths; a passkey needs no JobTrack TOTP

The complete authentication-choice model has exactly three paths:

1. username and password;
2. username, password, and TOTP when TOTP is enabled; or
3. a passkey alone.

There is no password-plus-passkey or passkey-plus-TOTP flow.

| Sign-in choice | TOTP disabled | TOTP enabled |
|---|---|---|
| Password | Establish session | Password, then `/Account/LoginTwoFactor` |
| Passkey | Establish session | Establish session; no additional TOTP |

A user-verified passkey is sufficient phishing-resistant authentication and replaces the
password-plus-TOTP sequence for that sign-in. This matches `SignInManager.PasskeySignInAsync`, which
completes with two-factor bypass, and reflects the requirement that the authenticator perform local
user verification (PIN, biometric, or equivalent). TOTP remains independently enrolled and continues
to protect password login. Adding or removing a passkey never enables or disables TOTP, and the
reverse holds. For ADR 0057 recent-authentication step-up, accept either the current password plus
TOTP (when enabled) or one owned user-verified passkey with no additional TOTP.

### 3. Multiple named passkeys, per-account maximum 10

An account may hold up to `MaxPasskeysPerAccount = 10` passkeys. Friendly names are required, 1–100
Unicode code points, and unique per account under ordinal-ignore-case comparison. A separately
stored `normalized_name`, derived once with invariant `ToUpperInvariant` normalization in every
command, carries that uniqueness; the database unique constraint compares the stored normalized
value exactly and never relies on PostgreSQL `lower`, SQLite `NOCASE`, or database locale to emulate
.NET comparison.

### 4. Registration policy

Registration uses `residentKey = required` and `userVerification = required`.
`authenticatorAttachment` is unset, so platform, synced, cross-device, and roaming credentials all
remain eligible.

### 5. No identifying attestation, no vendor allowlist

`attestation = none`. There is no authenticator/vendor allowlist. Enterprise attestation and
"hardware key only" policy are out of scope. AAGUID is stored but is never an authorization input.

### 6. Explicit RP ID and origin allowlist

Configure an explicit RP ID (`IdentityPasskeyOptions.ServerDomain`) and an exact HTTPS origin
allowlist (`ValidateOrigin`), never inferred from an untrusted Host header — no suffix matches,
wildcards, arbitrary forwarded hosts, or HTTP production origins. Production startup fails when the
feature is enabled without valid values matching the canonical HTTPS host. RP ID is a durable
credential namespace; changing it strands existing passkeys.

### 7. Credential transitions are atomic and secret-free

Add, remove, and reset each rotate the security and concurrency stamps, revoke PATs and other
sessions, write a secret-free audit event, and refresh only the initiating employee's current
cookie — one `IJobTrackClient` command, one ACID transaction. Rename is audited metadata and revokes
nothing. Razor Pages never coordinate those writes separately.

### 8. Recovery and lifecycle

Password change or reset does not silently remove passkeys. Account disablement blocks passkey
sign-in through `CanSignInAsync`. Separate administrator and Admin CLI **reset passkeys** operations
remove all credentials after loss or suspected compromise; they do not reset password or TOTP, and
password reset does not remove passkeys.

### 9. Username-less failures are generic and origin-throttled

Failed username-less assertions use generic responses and origin-based throttling. No account
lockout identity is invented before a cryptographically valid assertion identifies an account; a
client-supplied credential ID is never a limiter key, lockout target, or authorization input.

### 10. Configuration switch for rollout / rollback

`Authentication:Passkeys:Enabled` gates the feature. Disabling it hides enrolment and sign-in but
retains stored credentials and leaves password and TOTP available. Re-enable only with the same RP
ID.

## Consequences

- Spec §7.1's clause forbidding a functional passkey enrolment flow is superseded; §7.1 now describes
  passkeys as an optional primary credential and cross-references this ADR.
- `identity_user` gains a nullable `passkey_user_handle` (32 random bytes, base64url, unique),
  populated at new-account creation and lazily for existing accounts on first enrolment. A new
  `identity_user_passkey` table stores credential material under the credential boundary. PostgreSQL
  gets a forward-only schema version after `0026`; SQLite amends its fresh schema in place (ADR 0011).
- `JobTrack.Identity`'s hand-written store gains `IUserPasskeyStore<JobTrackIdentityUser>` (ADR 0022);
  JobTrack implements no `IPasskeyHandler<TUser>` and adds no third-party WebAuthn NuGet or JavaScript
  package.
- The server stores public-key credential material, not private keys or biometrics. ASP.NET Core's
  verified `UserPasskeyInfo` does not expose the authenticator AAGUID, so JobTrack records that
  optional database value as unknown instead of parsing attestation data itself or fabricating an
  identifier. JobTrack applies the
  spec's stronger secret-handling rule: no passkey field appears in ordinary employee queries, logs,
  traces, audit payloads, exports, error detail, or reporting-role grants.
- `docs/threat-model/web-authentication-threat-model.md`, `docs/behaviour-overview.md`,
  `docs/database-entities.md`, `docs/traceability/test-catalogue.md`, and the deployment/authentication
  sections of `docs/developer-guide.md` are amended alongside this ADR.
- WebAuthn credential-provider signal payloads (`signalUnknownCredential`,
  `signalAllAcceptedCredentials`) are not hand-assembled in this increment; the server-removal warning
  is the honest behaviour until ASP.NET Core exposes supported signal-option generation.
