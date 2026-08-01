# ADR 0057: Step-up authentication for sensitive operations, and an absolute session ceiling

**Status:** Accepted
**Date:** 2026-08-01
**Closes:** Security audit remediation plan §2.2, §2.3.

## Context

The authentication cookie is issued with `ExpireTimeSpan = 8h` and `SlidingExpiration = true`. ASP.NET
Core reissues the cookie with a fresh 8-hour window once the current one passes its halfway point, so a
regularly used cookie renews indefinitely — there is no absolute lifetime, only an idle timeout. A
stolen-but-still-live cookie can therefore be replayed for as long as the victim (or the attacker,
using it) keeps making requests.

Separately, no page distinguishes "signed in" from "recently authenticated." PAT issuance, enabling or
disabling TOTP two-factor, an administrator's password reset / two-factor reset / account
enable-disable, and role assignment all accept a bare authenticated cookie with no proof that the
credential was presented recently. A stolen cookie is therefore sufficient to mint a long-lived bearer
credential or bind an attacker-controlled second factor, not just to browse the app.

An earlier attempt (documented inline in the remediation plan, §2.3) tried to carry both timestamps as
claims on the `ClaimsPrincipal`, set via an extra `SignInWithClaimsAsync` call after
`PasswordSignInAsync`/`TwoFactorAuthenticatorSignInAsync`. It does not survive: `Program.cs` sets
`SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero`, so the security stamp validator
rebuilds the `ClaimsPrincipal` from `IUserClaimsPrincipalFactory` on every single request, discarding
any claim that factory does not itself add. Every claims-based variant tried lost the timestamp within
one request of sign-in.

## Decision

Store both timestamps in `AuthenticationProperties.Items`, not as claims. The security stamp
validator's regeneration path (`CookieValidatePrincipalContext.ReplacePrincipal` +
`ShouldRenew = true`) replaces the principal but reuses the *same* `AuthenticationProperties` instance
read off the incoming ticket, so `Items` survives every request — unlike the claims principal, which is
rebuilt from scratch.

- **`SessionAuthenticationInstants`** (`JobTrack.Web`) is the sole reader/writer of two `Items` keys:
  - `origin` — set once, at the start of a session, and never advanced by anything short of a fresh
    sign-in. This is the absolute-ceiling anchor (§2.3).
  - `recent` — set at sign-in and refreshed by step-up confirmation (§2.2). This is the
    recent-authentication freshness anchor.
- **`JobTrackSignInManager`** overrides the one `SignInWithClaimsAsync(TUser, AuthenticationProperties,
  IEnumerable<Claim>)` overload that every public sign-in entry point (`PasswordSignInAsync`,
  `TwoFactorAuthenticatorSignInAsync`, `RefreshSignInAsync`) funnels through in the base
  `SignInManager<TUser>`. Before delegating, it reads the *current* ticket's `origin` (if any) and
  passes it through unchanged; if there is none — a brand-new session — `origin` is stamped to now.
  `recent` is always stamped to now, because every call into this funnel already represents an event
  that itself constitutes fresh authentication: a password sign-in, a completed two-factor challenge,
  or an explicit `RefreshSignInAsync` call that every existing call site (`ChangePassword`,
  `ManageTwoFactor`) makes only after independently verifying the current password or TOTP code. A new
  explicit step-up confirmation page (`/Account/ConfirmAccess`) reaches the same funnel through
  `RefreshSignInAsync` after its own password/TOTP check.
- **Absolute ceiling.** `Program.cs` wraps the `OnValidatePrincipal` delegate that `AddIdentityCookies()`
  already points at `SecurityStampValidator.ValidatePrincipalAsync`, running the security stamp check
  first and then rejecting the principal (forcing sign-out) if `now - origin` exceeds
  `AbsoluteSessionCeiling` (8 hours — unchanged from today's advertised lifetime; sliding renewal no
  longer extends it past that point). A principal with no `origin` at all (a ticket issued before this
  change shipped) is treated as already expired — acceptable pre-release, since nothing has shipped
  yet (see the plan's scope note); it simply forces one extra sign-in.
- **Recent-authentication window.** `RecentAuthenticationWindow` (15 minutes) is the freshness bar for
  step-up-gated operations. `[RequiresRecentAuthentication]` marks the specific page handlers this
  finding names: PAT issuance, two-factor enable/disable, and — on the administrator side — employee
  creation, password reset, two-factor reset, account enable/disable, and PAT revoke-all, plus role assignment. A global
  `RequiresRecentAuthenticationPageFilter` (registered the same way as the existing
  `RequiresPasswordChangePageFilter`) inspects the selected handler method for the attribute and, if
  `now - recent > RecentAuthenticationWindow`, redirects to `/Account/ConfirmAccess?returnUrl=...`
  instead of invoking the handler.
- **`/Account/ConfirmAccess`** re-collects the current password (and, if the account has two-factor
  enabled, a TOTP code) for the already-signed-in user. Every submission consumes the shared login
  limiter's per-account-and-origin and per-origin budgets before either credential is checked, so a
  valid password cannot be used to make unbounded TOTP guesses. A successful check then calls `RefreshSignInAsync` — which, via the
  `JobTrackSignInManager` override above, refreshes `recent` while leaving `origin` untouched — and
  redirects back to `returnUrl` (validated with `Url.IsLocalUrl`, matching every other post-login
  redirect in this codebase).
- Two separate ceilings, not one: the absolute session ceiling (8h) bounds the whole session; the
  recent-authentication window (15min) bounds how long a step-up confirmation stays "fresh" for a
  *different* sensitive action. Confirming access once does not extend the session itself, and the
  session's absolute ceiling is not reset by a step-up confirmation — an administrator working for
  seven hours straight still hits the 8-hour wall even if they confirmed access five minutes ago.

## Consequences

- A stolen cookie's replay window is now bounded at 8 hours from the original sign-in, regardless of
  activity.
- A stolen cookie alone cannot mint a PAT, enable/disable two-factor, or perform the listed
  administrator actions — it must additionally reproduce the current password (and TOTP, once
  enrolled).
- `docs/threat-model/web-authentication-threat-model.md` row 3 is updated to describe idle, renewal,
  and absolute timeouts separately, and to note the step-up bar on the listed sensitive operations.
- No claims-based design is attempted again for this purpose; a future contributor who needs
  request-scoped authentication metadata that must survive the zero-interval security stamp
  revalidation should default to `AuthenticationProperties.Items`, not the `ClaimsPrincipal`.
- This does not add a distinct "elevated administrator session" concept — the recent-authentication
  window is a per-operation freshness check, not a separate elevated authentication state with its own
  session lifetime. If a future finding calls for that, it is a new ADR, not a silent extension of this
  one.
