# Hurl smoke tests

`tests/hurl/*.hurl` exercises the external HTTP API and the Razor Pages web interface over a real
HTTP connection against a real, already-running `JobTrack.Web` process — the one thing none of the
xUnit suites do. `JobTrack.Web.IntegrationTests` and `JobTrack.Web.EndToEndTests` drive an
in-memory `WebApplicationFactory`/`TestServer` or a spawned production-host child process
respectively (see `docs/operations/browser-testing.md`), but neither is a plain HTTP client hitting
a plain socket the way an external API consumer or a browser actually does. These four suites are
that gap: run them against a manually started dev server (or any deployed environment) as a
post-deploy/regression smoke check, not as a replacement for the xUnit gates.

## What each suite covers

- **`api-auth-required.hurl`** — every `/api/*` route (and the gated OpenAPI document) rejects an
  unauthenticated or bearer-garbage caller with the exact problem+json shape
  (`JobTrackApi.AuthenticationProblemType`) the cookie and bearer schemes both promise. No seed
  data or credentials needed; safe against any environment.
- **`web-smoke.hurl`** — the anonymous Razor pages render and every protected page redirects a
  signed-out visitor to sign-in rather than erroring or leaking content; also checks the security
  headers (spec §16) are present. No seed data or credentials needed.
- **`web-login-and-csrf.hurl`** — the real cookie + antiforgery flow: sign in, follow the mandatory
  forced-password-change redirect every freshly seeded/bootstrapped account gets
  (`JobTrackIdentityUser.RequiresPasswordChange`), then use the resulting session cookie plus a
  `GET /api/antiforgery-token` capture to make one CSRF-protected JSON write (claiming the UAT
  seed's unowned pickup-pool leaf via `POST /api/jobs/{id}/pickup`). **Requires a freshly seeded
  account that has never signed in** — not re-runnable against the same account without reseeding,
  since the forced password change it drives is a one-time transition.
- **`api-bearer-reads.hurl`** — the job-context read surface (`/api/jobs/root`, `/{id}`,
  `/{id}/children`, `/{id}/readiness`, `/jobs/search`) with a real bearer PAT. Requires a token for
  an account whose forced password change has already completed (`web-login-and-csrf.hurl` above,
  or any account that has signed in and changed its password before) — the API rejects a bearer
  token for an account that still has `RequiresPasswordChange` set
  (`RequiresPasswordChangeEndpointFilter`, 403 `/problems/password-change-required`), the same as it
  would reject the cookie scheme.

## Minting a bearer PAT for the read suite

There is no HTTP endpoint or dev-only shortcut that issues a personal access token — by design
(ADR 0029), the only issuance paths are the signed-in `/Account/PersonalAccessTokens` Razor page and
`JobTrack.AdminCli`'s `issue-token` command, both of which call `ITokenCommands.IssueAsync`
in-process:

```bash
dotnet run --project src/JobTrack.AdminCli -- issue-token \
  --provider sqlite --connection-string "Data Source=src/JobTrack.Web/jobtrack-web-dev.db" \
  --username priya.manager --label hurl-smoke --lifetime-days 1
```

This prints the plaintext token once (`Personal access token for '<username>': <token>`) — it is
never retrievable again. `--lifetime-days` defaults to 7 if omitted; the domain policy
(`PersonalAccessTokenPolicy.MaxLifetime`) caps it at 365.

## Running the suite

`scripts/run-hurl-tests.sh` runs all four suites in the dependency order above (auth/smoke checks
first, then the login+CSRF flow, then `issue-token`, then the bearer reads) against a host you have
already started. It does not start the host itself or seed the database — see README's "Running on
a development server" and "Seeding a synthetic end-user testing (UAT) scenario" for that sequence,
which must be freshly repeated before every run (the seed and the forced password change are both
one-time, non-idempotent transitions):

```bash
# 1. Deploy, bootstrap, and seed a fresh SQLite dev database (README sequence).
rm -f src/JobTrack.Web/jobtrack-web-dev.db*
dotnet run --project src/JobTrack.Database -- deploy --provider sqlite --connection-string "Data Source=src/JobTrack.Web/jobtrack-web-dev.db" --scripts-root database/sqlite/schema-versions
dotnet run --project src/JobTrack.AdminCli -- bootstrap --provider sqlite --connection-string "Data Source=src/JobTrack.Web/jobtrack-web-dev.db"
dotnet run --project samples/JobTrack.UatSeed -- --provider sqlite --connection-string "Data Source=src/JobTrack.Web/jobtrack-web-dev.db"

# 2. Start the host (a separate terminal/background process — it must stay running).
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/JobTrack.Web --urls "http://localhost:5034"

# 3. Run the hurl suite against it.
./scripts/run-hurl-tests.sh
```

Pass `--base-url`, `--provider`, `--connection-string`, `--username`, `--password`, or
`--new-password` to point the script at a different host, provider, or seeded account; see the
script's argument parsing for exact defaults (the UAT seed's `priya.manager` / known password by
default).

## Running an individual suite by hand

Each `.hurl` file documents its own `hurl --variable ...` invocation in its header comment. For
example, the auth-required suite needs nothing but a base URL:

```bash
hurl --test --variable base_url=http://localhost:5034 tests/hurl/api-auth-required.hurl
```
