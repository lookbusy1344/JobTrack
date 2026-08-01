# Security audit remediation plan

**Date:** 2026-08-01  
**Status:** Implemented. §2.1–2.5 landed first; §2.6 (PostgreSQL runtime-credential blast radius),
§2.7 (CLI secret exposure), §2.8 (login-limiter DoS), and §2.9 (PostgreSQL transport security) landed
in four further commits on 2026-08-01, in the §3 remediation order. A fresh-eyes follow-up then closed
the remaining implementation gaps: PAT-specific runtime roles, removal of weak-password and secret-argv
bypasses, mixed-requester and empty-read admission, retained limiter state under saturation, complete
step-up coverage/throttling, and hostname-verified remote TLS. §2.6 carries two documented,
intentionally deferred residuals rather than full closure — see its own section and the updated
threat model row 13: `identity_user` secret-column access remains shared between `jobtrack_domain`
and `jobtrack_identity` (command ports need it inside the same transaction as their audit row), and
`audit_event` access stays a direct table grant rather than a `SECURITY DEFINER` function (the
administrator audit-history view reads it through the same connection). §2.9's live-SSL integration
test against a real trusted CA was scoped out (validator-level unit tests only); §2.7's process-level
child-process argv/stdout inspection was scoped out (testable-abstraction unit tests only). Not yet
accepted as phase-gate evidence — that requires updating the impl plan's own gate tracking separately.  
**Scope:** Fresh audit of the current database contracts, PostgreSQL deployment role, reusable
library boundary, Identity adapter, external HTTP API, Razor Pages host, administrator CLI, runtime
configuration, and dependency posture. The two fixed passwords in the demo Docker image are
explicitly excluded at the owner's direction. The same exclusion does not extend to password policy,
credential handling, or PostgreSQL deployment practices used outside that demo.

## 1. Current assessment

The application has a stronger security baseline than most projects at this stage:

- Razor Pages are closed by default, every operational page and API route has an explicit policy,
  cookie-backed mutations use antiforgery, and library mutations reload authoritative roles and
  account state inside their transaction.
- Authentication cookies are `Secure`, `HttpOnly`, and `SameSite=Lax`; security stamps are checked
  on every request; account and role changes revoke sessions and PATs; bearer failures and API
  exception responses are redacted.
- The host fails closed in Production when trusted proxies, allowed hosts, or a persistent Data
  Protection key path are absent. It also sets restrictive CSP/frame/MIME/referrer/cache headers,
  bounds request bodies and execution time, and has real-Kestrel evidence for production-only
  controls.
- PATs have 256 bits of generated entropy, are stored only as a one-way hash, have bounded expiry,
  are shown once, and are individually and administratively revocable.
- PostgreSQL prevents the application role from DDL, audit/history deletion, and audit updates;
  the reporting role cannot read password, security-stamp, TOTP-key, or PAT-hash columns.
- EF parameterization is used consistently. The reviewed raw SQL is either fixed text,
  interpolated through EF/Npgsql parameters, or a source-controlled stored function. Razor output
  remains encoded and client-side history uses `textContent`, not HTML injection APIs.
- `gtimeout 120 dotnet list JobTrack.slnx package --vulnerable --include-transitive` reported no
  known vulnerable direct or transitive package from the configured NuGet source on 2026-08-01.

No critical issue was found in the deployed HTTP request path. The findings below are material
because they weaken credential assurance, allow supported in-process/operational paths to bypass the
security story asserted for the web path, or contradict the PostgreSQL compromise model.

Severity order is **Critical** > **High** > **Medium** > **Low**.

## 2. Findings

### 2.1 Password verification policy accepts six-character passwords and omits a compromised-password blocklist

| | |
|---|---|
| **Severity** | **High** |
| **Evidence** | `src/JobTrack.Abstractions/PasswordPolicy.cs`; `src/JobTrack.Identity/ServiceCollectionExtensions.cs`; `src/JobTrack.Application/AccountCredentialCommands.cs` |

`PasswordPolicy.MinimumLength` is `6`, requires one letter and one digit, and is reused by bootstrap,
employee creation/reset, and self-service password change. A password such as `aaaaa1` therefore
satisfies the product-wide policy. There is no comparison against common, context-specific, or
previously compromised passwords.

This is not a complaint about the Docker demo credentials. It is the verifier used by real accounts.
MFA is optional, so every account must be treated as password-only when the password is established.
The current [NIST SP 800-63B password-verifier requirements](https://pages.nist.gov/800-63-4/sp800-63b.html#passwordver)
require at least 15 characters for a password used as a single factor, no character-class composition
rules, acceptance of at least 64 characters, and comparison against a blocklist of common or
compromised values.

Remediation:

1. Add failing shared policy, application, Identity, Admin CLI, and web tests before changing the
   policy. Prove every password-setting route uses the same verifier.
2. Replace the six-character composition rule with a minimum of at least 15 Unicode code points,
   allow spaces and all printable input, accept at least 64 characters, and stop requiring a letter
   or digit.
3. Add a local, deterministic password blocklist service at the application boundary. It must cover
   common/breached values plus context-specific values such as `JobTrack` and the username. Do not
   send plaintext passwords to a third party during account creation or password change.
4. Decide in an ADR whether existing accounts are forced through a password change at next login.
   Hashes cannot reveal whether an existing password meets the new policy, so this needs an explicit
   rollout decision rather than an ineffective database inspection.
5. Add maximum-size and normalization tests so a policy improvement cannot introduce password
   truncation or inconsistent hashing between Identity and application commands.

### 2.2 Credential and administrator operations do not require recent or stepped-up authentication

| | |
|---|---|
| **Severity** | **High** |
| **Evidence** | `src/JobTrack.Web/Pages/Account/PersonalAccessTokens.cshtml.cs`; `src/JobTrack.Web/Pages/Account/ManageTwoFactor.cshtml.cs`; `src/JobTrack.Web/Pages/Admin/ManageEmployeeAccount.cshtml.cs`; `src/JobTrack.Web/Pages/Admin/AssignRole.cshtml.cs` |

An authenticated cookie is sufficient to mint a PAT lasting up to 365 days. PAT issuance asks for
neither the current password nor an existing second factor. Enabling a new TOTP authenticator proves
possession only of the newly displayed secret, not possession of an already enrolled factor or the
account password. Administrator password resets, 2FA resets, enable/disable actions, PAT revocation,
and role assignment likewise rely only on the existing session.

A stolen but otherwise valid cookie can therefore be converted into a long-lived bearer credential,
bind an attacker-controlled second factor, or perform high-impact account administration. CSRF does
not mitigate session theft. OWASP's authentication and MFA guidance calls for reauthentication on
sensitive features and with an existing factor before changing enrolled authenticators.

Remediation:

1. Record an ADR defining sensitive operations and a named recent-authentication window. Include PAT
   issuance, 2FA enable/disable/reset, password reset, role assignment, account enable/disable, and
   administrator revocation actions.
2. Add a server-validated authentication-time claim or equivalent protected session state. Do not
   trust a form field or filter/session convenience store as the authority.
3. Require the current password for password-authenticated users and, where 2FA is already enabled,
   require the existing TOTP factor as well before binding or removing a factor. Define a deliberate
   emergency/admin recovery exception rather than silently treating an old cookie as step-up.
4. Make PAT issuance fail before reserving or minting plaintext when recent authentication is absent.
   After step-up, regenerate the authentication ticket and preserve only principal-bound,
   non-sensitive convenience state intentionally.
5. Add stolen-cookie integration tests for every sensitive operation, including a positive path for
   recent authentication and a test that a successful PAT cannot outlive the account transition
   rules already implemented.

### 2.3 The advertised eight-hour authentication lifetime has no absolute ceiling

| | |
|---|---|
| **Severity** | **Medium** |
| **Evidence** | `src/JobTrack.Web/Program.cs`; `docs/threat-model/web-authentication-threat-model.md` row 3 |

The application sets `ExpireTimeSpan` to eight hours and `SlidingExpiration = true`. ASP.NET Core
reissues a cookie with a new expiry after it passes halfway through the current window. A regularly
used cookie can therefore remain valid indefinitely unless its security stamp changes, despite the
threat model describing a bounded lifetime.

This widens the replay window for an unnoticed stolen cookie and compounds finding 2.2. An idle
timeout is useful, but it is not an absolute lifetime.

Remediation:

1. Add a failing integration test that advances a controllable clock through repeated sliding
   renewals and proves the session eventually expires at an absolute boundary.
2. Preserve an immutable original-authentication instant in the protected ticket and reject tickets
   beyond a named absolute maximum in cookie validation. Keep a shorter sliding idle window if the
   usability requirement remains.
3. Decide separate absolute ceilings for ordinary and elevated/recent-authentication state. Do not
   make administrator elevation last for the whole ordinary session.
4. Update the threat model and operational documentation to distinguish idle, renewal, and absolute
   timeouts.

**Implementation note (attempted 2026-08-01, reverted — no code landed).** A claims-based design for
both the absolute-origin instant (§2.3) and the step-up/recent-authentication instant (§2.2) does
**not work in this codebase** and should not be re-attempted as written. `Program.cs` sets
`SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero`, so ASP.NET Core Identity's
security-stamp validator revalidates on *every* request and rebuilds the `ClaimsPrincipal` from the
claims factory each time — silently dropping any custom claim added by a prior sign-in, an overridden
`SignInManager.SignInWithClaimsAsync`, or an explicit extra `SignInWithClaimsAsync` call after
`PasswordSignInAsync`/`TwoFactorAuthenticatorSignInAsync`. This was confirmed by direct instrumentation
(exception-based, not `Console` — `Console.Error.WriteLine` from inside the in-process
`WebApplicationFactory` host does not surface into the `dotnet test` output at all in this harness,
which cost significant time to discover). Every approach tried lost the claim within one request of
sign-in.

The next attempt should store both instants in `AuthenticationProperties.Items` (which the security
stamp validator's ticket regeneration preserves, unlike claims) rather than as claims on the
`ClaimsPrincipal`. That changes: how `RecentAuthenticationClaims`-equivalent read/write helpers access
the value (via `HttpContext.AuthenticateAsync(...).Properties.Items[...]` rather than
`ClaimsPrincipal.FindFirstValue`), how the absolute-ceiling check in `OnValidatePrincipal` reads it
(`context.Properties`, already available on `CookieValidatePrincipalContext`), and how a step-up
confirmation (`/Account/ConfirmAccess`) writes a refreshed value without touching the origin one.
Re-verify early, with a real request through the full pipeline (not a unit test), that the value
survives one round trip before building the rest of the feature on top.

### 2.4 Public read operations do not consistently authenticate the `CommandContext` actor

| | |
|---|---|
| **Severity** | **High** at the reusable-library boundary; HTTP is currently shielded by host authorization |
| **Evidence** | `src/JobTrack.Application/JobQueries.cs`; `src/JobTrack.Persistence.PostgreSql/PostgreSqlJobBrowseQueryPort.cs`; `PostgreSqlReadinessQueryPort.cs`; `PostgreSqlLeafWorkQueryPort.cs`; `PostgreSqlPrerequisiteQueryPort.cs`, with equivalent SQLite ports |

`CommandContext.Actor` is caller-supplied. Mutations and sensitive reads generally reload the actor's
Identity row, call `ActorAccountState.EnsureMayAct`, and load current roles. Several public query
methods do not:

- job node, children, search, summaries, subtree, branch achievement, readiness, awaiting-progress,
  leaf-work, and prerequisite reads pass directly to ports that do not receive an actor;
- `GetEmployeeDirectoryAsync` and `GetAllEmployeesAsync` ignore the request actor entirely; and
- `GetCostFilterRolesAsync` deliberately converts a missing actor into an empty role set while still
  returning the underlying universally browsable job data.

The product decision that job data is visible to every baseline employee does not mean it is visible
to a nonexistent, disabled, locked, role-less, or forged actor. An in-process consumer can construct
such a context and read internal data through the public `IJobTrackClient` facade. This contradicts
the documented rule that the library is the authoritative authorization boundary rather than the
web policy layer.

Remediation:

1. Add provider-conformance tests first for nonexistent, disabled, locked, role-less, and
   Requester-only actors across each query capability. Do not duplicate one test per superficial
   facade method; define a table of read-capability classes and exercise representative calls plus an
   architecture coverage check.
2. Add one reusable actor-access query returning current account state and roles, backed by each
   provider. Authenticate once at the top of each public query composition, then pass the resulting
   immutable access context to any capability-specific policy.
3. Define an explicit `CanBrowseJobData` policy for the six baseline employee roles. Keep Requester
   access on requester-safe projections only. Preserve accepted ownership semantics; this finding is
   about authenticating admission, not narrowing ordinary employees to owned subtrees.
4. Require Administrator for `GetAllEmployeesAsync`; apply the intended baseline-employee admission
   to the workflow directory. Verify every direct library host, including Admin CLI and samples,
   still supplies a real actor.
5. Add an architecture test that every public `IJobQueries` member declares and invokes an admission
   category so a new read cannot silently bypass account-state validation.

### 2.5 `AdminCli issue-token` impersonates the target user and misattributes issuance in the audit trail

| | |
|---|---|
| **Severity** | **High** |
| **Evidence** | `src/JobTrack.AdminCli/IssueTokenCommand.cs`; `src/JobTrack.Application/TokenCommands.cs`; `src/JobTrack.Persistence.PostgreSql/PostgreSqlPersonalAccessTokenPort.cs`; ADR 0029; `docs/api/external-http-api-reference.md` |

The command resolves any supplied username, constructs `CommandContext.Actor` from that target, and
calls the self-service issuance operation with actor equal to target. It authenticates no human
administrator and carries no separate operator identity. The resulting audit event records the
target user as though that user issued the credential.

This bypasses `PersonalAccessTokenAccessPolicy.CanIssue` by manufacturing the exact self-service
shape the policy accepts. It also conflicts with ADR 0029's rule that a PAT authenticates as the user
who created it and with the threat model's claim that issuance is reached through an existing
authenticated session. The API reference later says administrators may issue through Admin CLI,
but no accepted ADR defines that delegation or its audit semantics.

Remediation:

1. Stop treating the current command as valid evidence for PAT provisioning. Add failing tests that
   an unauthenticated operator cannot mint for an arbitrary user and that an administrative issuance
   is never audited as self-service.
2. Choose one design in an ADR:
   - remove `issue-token` and require the user to issue through the stepped-up self-service page; or
   - define explicit administrator-delegated issuance with a distinct command, authorization policy,
     actor identity, audit operation, reason, shorter maximum lifetime, and secure handoff.
3. If operational smoke tests need a token, seed one only inside isolated test fixtures or use a
   dedicated test-only host path unavailable in published artifacts. Test convenience is not a
   production authorization model.
4. Update ADR 0029, the threat model, and API reference together; they currently describe different
   authorities.

### 2.6 PostgreSQL's normal runtime credential has a larger secret and audit-integrity blast radius than the threat model states

| | |
|---|---|
| **Severity** | **High** |
| **Evidence** | `database/postgresql/roles/jobtrack-roles-and-grants.sql`; `src/JobTrack.Web/Program.cs`; `tests/JobTrack.Database.ContractTests/PostgreSqlRoleGrantsTests.cs`; threat-model row 13 |

`JobTrack.Web` uses one connection string for Identity and every domain/PAT operation. The
`jobtrack_application` role consequently has table-wide `SELECT`, `INSERT`, and `UPDATE` on
`identity_user`, table-wide `SELECT`, `INSERT`, and `UPDATE` on `personal_access_token`, and direct
`INSERT` on `audit_event`.

Compromise of that normal runtime database credential therefore permits:

- exfiltrating password hashes for offline cracking and encrypted TOTP blobs for later attack (the
  TOTP plaintext still requires compromise of the separately persisted Data Protection keys);
- reading every PAT hash, inserting a chosen PAT hash, extending/reassigning tokens, or resetting
  their last-used/revocation state; and
- inserting fabricated audit events attributed to any existing user.

Preventing DDL and audit deletion is useful but does not provide the credential isolation or
audit-integrity property the threat model currently claims. The readonly and emergency roles are
well constrained; the normal application's credential is the gap.

Remediation:

1. Add negative PostgreSQL role tests that express the desired end state before changing grants:
   the ordinary domain role cannot read credential columns or PAT hashes, cannot insert/update PAT
   secrets directly, and cannot insert arbitrary audit rows.
2. Split runtime connections/roles by capability at minimum into domain data, Identity credential
   verification/maintenance, and PAT authentication/management. Do not merely put three passwords
   in the same unrestricted configuration object and call that isolation; scope each grant to the
   queries and columns its component needs.
3. Encapsulate PAT lookup/last-used update and audit append behind source-controlled PostgreSQL
   functions with narrow parameters and carefully reviewed `SECURITY DEFINER` ownership/search-path
   handling, or choose an equivalent design that removes direct secret-table and audit-table access.
4. Ensure audit attribution comes from authenticated application context and that the database
   rejects arbitrary actor/operation insertion by the ordinary role. This may require a transaction-
   local trusted context set only through the narrow write function.
5. Add role-login integration tests, not only `SET ROLE` grants inspection, and update backup,
   rotation, and incident-response runbooks for the split credentials.
6. If this split is rejected as disproportionate for the single-server threat model, amend the
   threat model and obtain explicit risk acceptance. Do not retain the current claim of isolation.

### 2.7 Operational CLIs expose database credentials and initial passwords in process arguments

| | |
|---|---|
| **Severity** | **Medium** |
| **Evidence** | `src/JobTrack.Database/DeployCommandOptions.cs`; every `*CommandOptions.cs` in `src/JobTrack.AdminCli`; README and operations examples |

Every database/admin command requires `--connection-string`. A production PostgreSQL connection
string commonly contains a password, so it is copied into shell history and may be visible in
process listings and job-runner metadata. `create-employee` also requires `--password`; bootstrap
accepts it optionally. The source comments acknowledge this for passwords but offer no secure
non-interactive alternative, and they do not address the database secret exposed on every command.

Remediation:

1. Add parser tests first for mutually exclusive secret sources and for redacted error/usage output.
2. Make connection acquisition use standard PostgreSQL mechanisms: a password file/passfile,
   integrated identity where available, a secret-store-provided file descriptor, or a named
   environment/configuration key whose value is never echoed. Prefer a passfile over `PGPASSWORD`,
   which may itself be process-environment-visible.
3. Make initial employee passwords masked interactive input by default. For automation, accept
   stdin or an inherited file descriptor with explicit one-use semantics; do not accept plaintext on
   `argv` in production workflows.
4. Deprecate and then remove secret-bearing flags from published usage. Keep any Docker-demo-only
   compatibility inside the demo build/run script, clearly isolated from the production CLI.
5. Add process-level tests that inspect the child command line and captured stdout/stderr to prove no
   database password, initial password, temporary password, or PAT is present except the deliberately
   one-time secret on its designated secure output channel.

### 2.8 The login limiter's global partition cap can be turned into cross-user denial of service

| | |
|---|---|
| **Severity** | **Medium** |
| **Evidence** | `src/JobTrack.Web/LoginAttemptRateLimiter.cs`; `src/JobTrack.Web/Program.cs` |

The limiter stores password and 2FA partitions in process-wide dictionaries with a shared maximum of
4,096 entries. `WouldExceedPartitionLimit` rejects every previously unseen key once that count is
reached. An attacker using varied usernames and source addresses can therefore consume all slots;
legitimate users whose address/username pair has not already appeared are rejected until expiry.
Every attempt also walks both dictionaries and locks each state while pruning, making request cost
linear in attacker-created partitions.

The per-address backstop means one address cannot fill the table alone, but this remains practical
for a distributed credential-stuffing source set and turns a memory bound into an authentication
availability switch.

Remediation:

1. Add deterministic concurrency/time-provider tests proving attacker-created partitions cannot
   deny a distinct legitimate partition and pruning is not an O(partition-count) operation on every
   request.
2. Replace the dictionaries with a bounded cache that expires independently and evicts an attacker
   partition rather than fail-closing every unseen user. Preserve per-address-plus-username and
   per-challenge partitioning plus the global backstop.
3. Keep unknown usernames from creating unlimited durable state. Hash bounded normalized keys if a
   shared store is later introduced, so usernames do not become operational cache keys/log data.
4. Re-run the multi-instance plan's distributed-limiter design against these abuse cases before
   adopting a shared implementation.

### 2.9 Remote PostgreSQL transport security is neither enforced nor documented as a production requirement

| | |
|---|---|
| **Severity** | **Medium** for a separate database host; not applicable to a same-host Unix socket |
| **Evidence** | `src/JobTrack.Web/Program.cs`; `src/JobTrack.AdminCli/Program.cs`; `src/JobTrack.Database/Program.cs`; `docs/operations/production-deployment.md` |

All PostgreSQL composition roots accept a connection string as supplied. Npgsql's documented default
is `SSL Mode=Prefer`, which neither guarantees encryption nor protects against a man in the middle.
The production runbook permits PostgreSQL on a separate private host but does not require
`SSL Mode=VerifyFull`, a trusted root certificate, or an equivalent authenticated GSS-encrypted
channel. A private network limits exposure; it does not authenticate the server or encrypt database
credentials and application data.

Remediation:

1. Add configuration tests for every PostgreSQL host (web, database deployer, Admin CLI). Outside
   Development, reject remote TCP connection strings that do not require an authenticated encrypted
   channel.
2. Permit an explicit same-host Unix-domain socket mode without TLS. Treat loopback TCP as a
   documented local exception only if the reverse-proxy/database topology keeps it host-local.
3. Update Linux/Windows deployment examples to use `SSL Mode=VerifyFull` plus a trusted root for
   remote PostgreSQL, or a documented GSS-encryption configuration with equivalent authentication.
   Do not recommend `Trust Server Certificate=true` as production remediation.
4. Add a real PostgreSQL transport test with a trusted test CA and negative tests for plaintext and
   wrong-host certificates. Verify backup/restore and schema-deployment tools use the same policy.

## 3. Remediation order

Use TDD for every slice. Preserve the mandatory database → library → HTTP API → Razor Pages order
where a change crosses layers.

1. **Close the supported credential-minting bypass (§2.5).** Decide the ADR before changing code;
   remove or redesign `AdminCli issue-token` and align documentation.
2. **Strengthen the verifier (§2.1).** This affects every new/reset credential and has a contained,
   testable boundary. Decide existing-account rollout in the same ADR/change set.
3. **Authenticate all library reads (§2.4).** Start with shared/provider contract tests, then the
   application facade, then rerun API/page authorization tests.
4. **Add step-up and absolute session limits (§2.2–§2.3).** Implement one coherent authentication
   event model rather than independent page-specific password prompts.
5. **Reduce PostgreSQL credential blast radius (§2.6).** Design roles/functions first, add grant and
   real-login tests, then split composition-root configuration and operations docs.
6. **Secure PostgreSQL transport and CLI secret input (§2.7, §2.9).** Coordinate connection-source
   changes with the split-role design so credentials are not migrated twice.
7. **Replace the login limiter store (§2.8).** Preserve existing abuse-case behavior while removing
   the global exhaustion and linear-pruning properties.
8. Update the threat model, API reference, test catalogue, production runbooks, and plan index only
   as each implemented slice becomes true.

Do not mark a plan item remediated merely because the HTTP host already compensates for a library
gap, or because possession of the current PostgreSQL credential already enables other damage. The
purpose of defense in depth is to prevent one compromised boundary from automatically becoming all
boundaries.

## 4. Verification gate

Each implementation commit runs the repository commit gate plus targeted tests for its slice. At
final close, run:

```bash
dotnet build JobTrack.slnx -warnaserror
dotnet format JobTrack.slnx
./scripts/fast-test.sh --build
gtimeout <chosen-long-budget> dotnet test tests/JobTrack.Database.ContractTests \
  --filter "FullyQualifiedName~PostgreSqlRoleGrantsTests|FullyQualifiedName~PostgreSqlTransportSecurityTests"
gtimeout <chosen-long-budget> dotnet test tests/JobTrack.Persistence.PostgreSql.Tests \
  --filter "FullyQualifiedName~ActorAdmission|FullyQualifiedName~PersonalAccessToken"
gtimeout <chosen-long-budget> dotnet test tests/JobTrack.Persistence.Sqlite.Tests \
  --filter "FullyQualifiedName~ActorAdmission|FullyQualifiedName~PersonalAccessToken"
gtimeout <chosen-long-budget> dotnet test tests/JobTrack.Web.IntegrationTests \
  --filter "FullyQualifiedName~Authentication|FullyQualifiedName~PersonalAccessToken|FullyQualifiedName~Admin"
gtimeout <chosen-long-budget> dotnet test tests/JobTrack.AdminCli.Tests \
  --filter "FullyQualifiedName~Secret|FullyQualifiedName~IssueToken"
gtimeout <chosen-browser-budget> dotnet test tests/JobTrack.Web.EndToEndTests \
  --filter "FullyQualifiedName~Authentication|FullyQualifiedName~PersonalAccessToken"
gtimeout 120 dotnet list JobTrack.slnx package --vulnerable --include-transitive
./scripts/all-test.sh
```

Choose and record explicit budgets when implementing; placeholders above are not runnable gate
commands. Run `./scripts/clean-test-databases.sh` after an interrupted database test.

Implementation evidence recorded on 2026-08-01:

- the warning-as-error build, formatter, and fast suite passed after every remediation slice;
- focused role-grant/transport tests passed (25), PostgreSQL and SQLite actor-admission/PAT tests
  passed (6 per provider), the web authentication/PAT/admin/step-up/limiter tests passed (76), the
  Admin CLI suite passed (110), and the focused PostgreSQL external-client proof passed;
- the transitive NuGet vulnerability scan reported no known vulnerable packages;
- a serialized final solution run passed every database, library, public-API, and architecture
  project, then passed 214 of 215 browser tests; the remaining PostgreSQL PAT reflow test timed out
  waiting for its form and is left for a clean manual rerun rather than recorded as a pass; and
- the real trusted-test-CA transport test and OS-level process-argument inspection remain explicitly
  deferred. Configuration/option tests prove fail-closed TLS validation and rejection/redaction of
  supported secret-bearing arguments, but do not substitute for those two environment-level tests.

## 5. Completion criteria

This plan is complete only when:

- every account-setting path enforces the new long-password/blocklist policy and the existing-account
  rollout is decided;
- PAT issuance and every sensitive account/admin transition requires a defined recent or stepped-up
  authentication event;
- authentication has both an idle/renewal policy and an enforced absolute lifetime;
- every public library read authenticates current actor account state and applies an explicit
  capability policy;
- no production CLI can impersonate a user to mint a PAT or misattribute an operator-issued
  credential as self-service;
- compromise of the normal PostgreSQL domain credential does not expose Identity/PAT secrets or
  permit arbitrary audit insertion, or the remaining exposure has explicit risk acceptance and
  accurate threat-model text;
- production CLI secrets are absent from process arguments and ordinary output;
- attacker-created login partitions cannot exhaust all new-user admission or make every request scan
  the full partition set;
- remote PostgreSQL connections fail closed without authenticated encryption; and
- targeted dual-provider, web, CLI, real-PostgreSQL, dependency, and final full-suite evidence is
  recorded in the traceability catalogue.
