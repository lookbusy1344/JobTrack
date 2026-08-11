# ADR 0055: Remove `AdminCli issue-token`; hurl smoke tests mint via self-service

**Status:** Accepted
**Date:** 2026-08-01
**Closes:** Security audit remediation plan §2.5.
**Depends on:** ADR 0029.

## Context

`JobTrack.AdminCli issue-token` resolves an arbitrary supplied username, builds
`CommandContext.Actor` equal to that target user, and calls `ITokenCommands.IssueAsync` — the same
operation the self-service `/Account/PersonalAccessTokens` Razor page uses for a signed-in user
issuing their own token. Because `PersonalAccessTokenAccessPolicy.CanIssue` only ever authorizes
self-issuance, the command satisfies it by manufacturing the self-issuance shape for a user who
never authenticated. No human administrator identity is captured, and the resulting audit event
records the target user as though they issued the credential themselves — contradicting ADR 0029's
rule that a PAT authenticates as the user who created it, and understating who actually minted it.

The command's only real consumer is `scripts/run-hurl-tests.sh`, which needs a bearer PAT to drive
`tests/hurl/api-bearer-reads.hurl` without a browser. That script already runs
`tests/hurl/web-login-and-csrf.hurl` immediately beforehand, which performs a real login (with the
forced first-sign-in password change) and holds a live, cookie-authenticated, antiforgery-capable
session against the same account. No other script, test, or documented operational procedure
depends on `issue-token`.

## Decision

- Delete `JobTrack.AdminCli issue-token` entirely: `IssueTokenCommand`, `IssueTokenCommandOptions`,
  its `Program.cs` wiring and usage text, and `IssueTokenCommandTests`. This is not a redesign as
  administrator-delegated issuance — that would introduce a new privileged capability
  (administrators minting tokens for other users) that does not exist today and is not required by
  any current consumer.
- `scripts/run-hurl-tests.sh` mints its smoke-test token through the self-service page instead,
  reusing the session `web-login-and-csrf.hurl` already established: a GET of
  `/Account/PersonalAccessTokens` to capture its antiforgery token, then a POST to
  `?handler=Issue` with `Issue.Label`/`Issue.LifetimeDays` form fields, capturing
  `IssuedPlaintextToken` out of the rendered page. This is now the only issuance path exercised
  end-to-end, matching production reality: nothing other than an authenticated session can mint a
  PAT.
- No new authorization surface, actor-identity concept, or audit-operation category is added.
  `ITokenCommands.IssueAsync` keeps its existing single meaning: the caller mints a token for
  themselves.
- Operational tooling and CI that need a token going forward must authenticate as a real user (a
  seeded test/service account is acceptable) and issue through the self-service page or an
  equivalent authenticated HTTP call — never by constructing an actor from an unauthenticated
  process.

## Consequences

- Closes the credential-minting bypass: no in-process or CLI path can mint a PAT for a user who has
  not authenticated.
- `docs/operations/hurl-smoke-tests.md` and `README.md` lose their `issue-token` references and gain
  the self-service-page sequence instead.
- If a genuine future need for administrator-delegated PAT issuance emerges (e.g. provisioning a
  service account with no interactive login of its own), it requires its own ADR defining a distinct
  command, authorization policy, actor identity, audit operation, reason, and shorter maximum
  lifetime — not a revival of this command's shape.
