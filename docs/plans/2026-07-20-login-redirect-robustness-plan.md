# Login Redirect Robustness Plan

**Date:** 2026-07-20
**Status:** App-layer fixes implemented (commits `ab8f0df`, `64bad8a`, `b894810`). Infra follow-up
open.
**Scope:** Investigation and remediation of a reported intermittent login failure on the Cloud Run
demo — "sometimes after logging in (especially with a password manager) the site leaves me on the
Login page; the URL `/Account/Login` is a totally blank, zero-byte page." Covers the diagnosis, the
three landed app-layer fixes, and the remaining infrastructure root cause.

## 1. Symptom

Reported against the throwaway Cloud Run demo
(`https://jobtrack-web-716005672573.europe-west1.run.app`, `scripts/deploy-cloudrun.sh`):

- After entering valid credentials, the browser is parked on `/Account/Login` showing a **zero-byte**
  body (not the login form, not `/Index`).
- Pressing refresh prompts "resubmit form?"; confirming reproduces another zero-byte page.
- Editing the URL to the site root **does** grant access — i.e. the auth cookie was set; sign-in
  had actually succeeded.
- Intermittent, and correlated with password-manager use.

## 2. Diagnosis (evidence-based)

Reproduced directly against the deployment with `curl` (cookie jar + real antiforgery token). Key
findings:

- A valid login `POST /Account/Login` returns `302 → location: /` and sets
  `.AspNetCore.Identity.Application` — sign-in works; the happy path is fine.
- `GET /` is itself a redirect: anonymous → `/Account/Login`; authenticated →
  `/Jobs/Browse?ArchiveFilter=ActiveOnly`.
- Authenticated `GET /Account/Login` returned **200 with the form** — the login page did *not*
  redirect already-signed-in users away.
- A `POST /Account/Login` with a **missing/invalid antiforgery token** returns **HTTP 400 with
  `content-length: 0`** — a literally blank page, staying on `/Account/Login`.
- The auth cookie was `samesite=strict`.

Root-cause chain, reconciling every observation:

1. The blank page is the **empty body of a second, antiforgery-rejected login POST**. It is a POST
   result (hence the refresh-resubmit prompt) at `/Account/Login` with zero bytes (hence blank).
2. A **first** login POST had already succeeded and set the cookie (hence root access works after
   editing the URL). So there were two POSTs: one authenticated, one rejected.
3. The trigger for the second POST is two design facts combining:
   - **`SameSite=Strict`** withholds the auth cookie on externally-initiated top-level navigations —
     exactly how a password manager's "open & fill" launcher reaches the site — so an already-signed-in
     user arrives looking anonymous and is bounced to the login form.
   - **The login page rendered a live form even when already authenticated**, so a password manager
     detects a login form and auto-resubmits it. That second POST trips antiforgery and dead-ends on
     the zero-byte 400.
4. On the Cloud Run demo, the antiforgery/auth tokens go stale easily because the Data Protection key
   ring is **regenerated on every scale-to-zero cold start / redeploy** (no persistent volume,
   `--min-instances=0`). This is the environmental amplifier that makes stale-token rejections routine
   there; the app-layer fixes below make the app robust regardless.

Confirmed: routing and the two zero-byte response types (302 redirect body, 400 antiforgery body).
Inferred (not remotely reproducible): the exact double-submit timing of a real browser + password
manager.

## 3. Landed fixes (app layer)

All three are TDD'd in `AccountFlowTests`; each was committed separately.

- **§3.1 — Redirect authenticated users off the login page.** Commit `ab8f0df`.
  `LoginModel.OnGet` now redirects a signed-in visitor into the app (`returnUrl` if local, else
  `/Index`) via a shared `RedirectToApp` helper reused by the post-sign-in success path. Breaks the
  "password manager sees a form and re-submits" loop at the source.
  Test: `Requesting_the_login_page_while_already_authenticated_redirects_into_the_app`.

- **§3.2 — Graceful antiforgery recovery.** Commit `64bad8a`.
  `LoginModel` opts out of automatic antiforgery validation (`[IgnoreAntiforgeryToken]`) and validates
  in-handler via `IAntiforgery.IsRequestValidAsync`. A missing/stale token now redirects (302) back to
  a fresh login form with a "session expired, please try again" notice (TempData), instead of the
  framework's zero-byte 400 that browsers replay on refresh. No credentials are examined on the
  failure path, so the CSRF guarantee (a forged tokenless login POST never authenticates) is unchanged
  and now asserted explicitly (no auth cookie issued).
  Tests: `Posting_the_login_form_without_the_antiforgery_token_is_rejected` (strengthened; still
  `TC-WEB-AUTHN-006`), `A_stale_antiforgery_token_redirects_to_a_fresh_login_form_showing_a_retry_notice`.

- **§3.3 — Relax auth cookie `SameSite` from `Strict` to `Lax`.** Commit `b894810`.
  `Program.cs` `ConfigureApplicationCookie`. Lax still sends the cookie on top-level GET navigations
  (post-login redirect, external/bookmarked links, password-manager launchers) while withholding it
  from cross-site POSTs. CSRF on state-changing requests is enforced by the antiforgery token (threat
  model row 6), not this cookie's SameSite mode, so the CSRF surface is unchanged. This addresses the
  root trigger. Spec §7.1 requires an "appropriate `SameSite` policy" (not specifically Strict), so Lax
  remains compliant; threat model row 3 (session theft) is mitigated by `Secure`/`HttpOnly` +
  security-stamp revocation, not SameSite, so no regression.
  Test assertion updated: `..._sets_a_secure_httponly_samesite_cookie` now expects `samesite=lax`.

## 4. Open follow-ups

- **§4.1 — Regression sweep not yet run.** Fixes §3.2/§3.3 touch antiforgery wiring and cookie
  attributes. Run the security-adjacent classes before considering this closed:
  `ProductionSecurityConfigurationTests`, `SecurityHeadersTests`, `PersonalAccessTokenManagementTests`,
  `ManageTwoFactorTests`, `AdminAccountManagementTests` (plus the full suite / `./scripts/fast-test.sh`).

- **§4.2 — Infra root cause (Cloud Run key-ring ephemerality).** Not addressed; deliberately left,
  since the Cloud Run path is a documented throwaway smoke test (ADR 0014, `deploy-cloudrun.sh` banner:
  no persistent volume, DB resets on recycle). The app-layer fixes make login robust to it. If the
  demo should stop rotating Data Protection keys entirely, options:
  - `--min-instances=1` in `deploy-cloudrun.sh` — cheapest; keeps one instance warm so keys survive
    between form render and submit. A redeploy still rotates keys (acceptable: re-login after deploy).
  - Persist the key ring to durable storage (GCS-mounted volume, or `PersistKeysToGoogleCloudStorage`)
    — proper fix; survives every recycle.
  Production (single VM + volume-backed keys per ADR 0014) is unaffected either way.

- **§4.3 — Consider a browser E2E covering the password-manager double-submit** (the one scenario not
  reproducible via the integration `HttpClient`), if the `JobTrack.Web.EndToEndTests` harness can drive
  a stale-token resubmission.

## 5. Reproduction notes (for future reference)

- Empty-body 400 on missing token:
  `curl -s -o /dev/null -w "%{http_code} %{size_download}" -X POST $BASE/Account/Login --data "Input.UserName=demo&Input.Password=demo1234"`
  → `400 0`.
- Full valid-login flow: `GET /Account/Login` (save antiforgery cookie + extract
  `__RequestVerificationToken`), then POST with both → `302 location: /` + `Set-Cookie` auth cookie.
- A transient MSBuild/Razor source-generator corruption surfaced mid-work as thousands of bogus
  `.cshtml` "already contains a definition for 'asp'" errors on untouched files. Fix:
  `dotnet build-server shutdown`, then rebuild. Not a code issue.
