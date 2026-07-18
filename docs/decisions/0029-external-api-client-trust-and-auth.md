# ADR 0029: External HTTP API client trust model and authentication mechanism

**Status:** Accepted
**Closes:** `docs/plans/2026-07-09-external-http-api-plan.md` §3 items 1–3 (client class, authentication
mechanism, token lifetime/revocation)

## Decision

**Client class.** The first supported non-browser consumer is a first-party CLI operated by an
authenticated human, run against a *remote* JobTrack.Web deployment. This is distinct from the
existing `JobTrack.AdminCli`, which stays an in-process consumer of `JobTrack.Application` on the
same trusted host — per the plan's non-goals, a same-host tool has no reason to go over HTTP.
Mobile apps, machine-to-machine integrations, and third-party delegated clients are out of scope
until a concrete consumer is identified for one of them; this ADR does not preclude adding them
later.

**Authentication mechanism.** Opaque personal access tokens (PAT), issued per user, presented as
`Authorization: Bearer <token>` on every API request. A PAT authenticates as the user who created
it and carries no capability beyond that user's own authorization scope — it is not a service
identity or a delegated-consent grant.

- Tokens are generated server-side (cryptographically random, sufficient entropy to resist
  brute-force and enumeration) and shown to the caller exactly once at creation.
- Only a one-way hash of the token is stored (unsalted SHA-256); the plaintext is never persisted,
  logged, or retrievable after issuance. Unlike a password, a PAT has 256 bits of server-generated
  random entropy (`PersonalAccessTokenSecretGenerator`), so a per-token salt or pepper adds no
  meaningful resistance to guessing or rainbow-table correlation — every stored hash is already
  unique and infeasible to invert or precompute against. A per-token salt would also force
  `TryAuthenticateAsync` off its O(1) hash-equality lookup and onto a full-table scan re-hashing
  every unexpired token, for no offsetting security benefit. This deliberately diverges from how
  `JobTrack.Identity` salts passwords, because passwords are low-entropy human-chosen secrets and
  PATs are not (security review remediation §2.4).
- A PAT requires an explicit, bounded expiry set at creation (see below). There is no
  non-expiring token.

**Token lifetime and revocation.**

- Maximum lifetime at issuance is capped (a named constant in the API layer, not a magic number
  scattered across code); a token cannot be created with an expiry beyond that cap. This forces
  periodic re-issuance rather than a token living unattended indefinitely.
- Tokens are individually revocable at any time by their owner, and by an administrator acting on
  that user's account.
- Tokens are automatically revoked (not just soon-to-expire) on every security-sensitive account
  transition already required to revoke web sessions: account disablement, password reset,
  password change, role change, and emergency password reset (`docs/plans/jobtrack_impl_plan.md`
  §8.6/§9.1). A PAT is a credential of the same sensitivity class as a session cookie, so it gets
  the same revocation triggers, not a weaker set.
- Each PAT records last-used time to support audit and to let an owner recognise and revoke a
  stale or unexpected token.

**Authentication scheme composition.** The web host authenticates requests using two independent
schemes selected by request shape, not one scheme replacing the other:

- The existing cookie scheme (`IdentityConstants.ApplicationScheme`) continues to serve the
  server-rendered Razor Pages and same-origin JSON calls, with antiforgery enforcement unchanged.
- A new bearer-token scheme validates the PAT against its stored hash and maps it to the same
  `AppUserId`/`CommandContext` construction the cookie principal already uses, so library
  authorization, ownership, and data-sensitivity checks run identically regardless of which
  scheme authenticated the caller.
- Antiforgery validation applies only to the cookie scheme. A bearer token is never attached by a
  browser automatically, so it is not subject to the cross-site request forgery threat antiforgery
  tokens exist to mitigate; requiring an antiforgery token on bearer-authenticated requests would
  add friction without closing a real threat.

## Rationale

- A single, first-party, human-operated CLI consumer does not need OAuth 2.1/OIDC's
  delegated-consent machinery (no third party is acting on a user's behalf) or JWT
  access/refresh-token rotation (no mobile client requiring silent short-lived-token renewal
  without re-prompting the user). PAT is the minimal primitive that satisfies this consumer:
  simple to issue, simple to revoke, no additional signing-key or authorization-server
  infrastructure to operate and secure.
- mTLS is a poor fit for a human-operated CLI: it implies provisioning and distributing per-caller
  client certificates, which is disproportionate machinery for a token a person copies into a
  config file or environment variable.
- Reusing cookie-plus-antiforgery as the remote-client contract is explicitly ruled out by the
  plan (§3 item 2) because it forces a non-browser client to emulate a browser login and CSRF
  flow — PAT avoids that without requiring OAuth's larger build.
- Giving every security-sensitive account transition the same revocation trigger for PATs that it
  already has for sessions closes an otherwise-obvious gap: a disabled or compromised account
  would otherwise keep working over the API after its web session was cut off.

## Consequences

- A new persisted entity is needed for PATs (owner `AppUserId`, token hash, label, created-at,
  expires-at, revoked-at, last-used-at) with the same dual-provider (PostgreSQL/SQLite) support as
  every other persistence entity, and the same house-style constraints (immutable
  read/query-time projections, no mutable graph escaping the domain).
- `JobTrack.Application` needs command surface to issue, list, and revoke a caller's own tokens
  (and, for administrators, another user's tokens), following the existing command/`CommandContext`
  pattern rather than a bespoke path.
- Every existing revocation site for sessions (disablement, password reset/change, role change,
  emergency reset — `JobTrack.Identity`/`JobTrack.AdminCli`) needs a corresponding PAT-revocation
  call added; this is tracked as explicit work items in §4.2 of the external API plan, not an
  automatic consequence of this ADR.
- `JobTrack.Web`'s authentication registration needs a second scheme registered alongside
  `AddIdentityCookies()`, with policy-based scheme selection so existing Razor Pages routes are
  unaffected.
- Direct HTTP tests are required (per plan §4.2) for unauthorized, expired, revoked, disabled,
  wrong-role, wrong-owner, sibling-subtree, and sensitive-data-denied cases specifically for
  bearer-authenticated requests, in addition to the existing cookie-authenticated coverage.
