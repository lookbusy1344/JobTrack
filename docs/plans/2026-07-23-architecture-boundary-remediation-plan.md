# Architecture Boundary Remediation Plan

**Date:** 2026-07-23
**Status:** Proposed
**Scope:** Fresh-eyes review of layer responsibilities, compound mutations, reusable-library
boundaries, and dual-provider consistency. This plan records only current findings; it does not
reopen implemented product plans or the accepted phase gates.

## 1. Review verdict

The project has a sound dependency shape:

- `JobTrack.Domain` is independent of application and persistence concerns.
- `JobTrack.Application` exposes the provider-neutral `IJobTrackClient` facade.
- PostgreSQL and SQLite sit behind that facade and own their transaction mechanics.
- Razor Pages and the external HTTP API use `IJobTrackClient`; they do not contain domain SQL or
  reach into a JobTrack persistence context.
- Shared provider contract tests and provider-specific race tests give the two implementations a
  strong behavioural parity check.

The session-start example is implemented at the correct boundary. `StartSessionAsync`,
`StartWorkAsync`, and `ReopenAndStartWorkAsync` claim an unassigned node for
`WorkedByUserId` inside the same provider transaction, before the authoritative authorization
check. Both providers have contract and contention coverage for the claim, audit event, rollback,
and losing-racer outcome (ADR 0048).

The architecture is therefore good in its broad shape, but it is not yet consistently enforced.
Two security/workflow transitions are composed in the ASP.NET host, one internal service-provider
interface is exposed as public API, several expressible writes bypass EF, and the architecture
suite does not detect these cases.

## 2. Findings

Severity: **High** > **Medium** > **Low**.

### 2.1 Work-page write-up plus pause/complete is a split web-layer transaction

| | |
|---|---|
| **Severity** | **High** |
| **Category** | Interface / library boundary / atomicity |
| **Files** | `src/JobTrack.Web/Pages/Jobs/Work.cshtml.cs`, `src/JobTrack.Application/IWorkCommands.cs`, both `*WorkSessionCommandPort.cs` |

`OnPostFinishAsync` and `OnPostCompleteAsync` call `SaveWriteUpFirstAsync`, which performs a query
and `IJobCommands.EditAsync`, and then invoke `FinishSessionAsync` or `CompleteLeafAsync` as a
second mutation with a different correlation id and transaction. The source explicitly accepts the
partial outcome: the write-up can commit when pause/completion subsequently fails.

That is one submitted form and one user intent. Treating the node and leaf-work rows as separate
aggregates does not make the web host the correct coordinator, nor does it remove the requirement
that a compound write commit once. It also gives the browser workflow semantics that the HTTP API
cannot request through one library operation.

**Remediation:**

- Add a library command for “finish this session and update this leaf's write-up” rather than adding
  UI orchestration to the generic `FinishSessionAsync` primitive.
- Extend the existing `CompleteLeafAsync` composite with an optional, explicitly versioned write-up
  change. Use a nested request value so “no write-up change” is distinct from “clear the write-up.”
- Re-fetch the node's other full-replace fields inside the command transaction; do not make the
  interface round-trip hidden copies of mutable node fields.
- Authorize, compare both relevant versions, update the node/session/leaf-work rows, write all audit
  events under one correlation id, and commit once.
- Keep standalone `EditAsync` and standalone `FinishSessionAsync` for callers whose intent is
  genuinely one mutation.
- Expose equivalent optional compound semantics through the HTTP API, while retaining the existing
  simple session-finish endpoint for compatibility.

**TDD evidence required:**

1. Shared provider contract tests fail first and prove rollback of the write-up when session or
   achievement validation, authorization, version checks, or audit persistence fail.
2. PostgreSQL and SQLite race tests prove a concurrent node edit or session change yields no partial
   write-up/work-state result.
3. Web integration tests prove each ending handler invokes one mutation and reports a single
   correlated outcome.
4. API integration/OpenAPI tests prove browser and HTTP clients can request the same compound
   operation.

### 2.2 Self-service password change is orchestrated across independent web-layer writes

| | |
|---|---|
| **Severity** | **High** |
| **Category** | Security / interface / atomicity |
| **Files** | `src/JobTrack.Web/Pages/Account/ChangePassword.cshtml.cs`, credential/token/audit command ports |

The page currently performs `UserManager.ChangePasswordAsync`, clears
`RequiresPasswordChange` with an unchecked `UpdateAsync` result, refreshes the cookie, revokes PATs
through `IJobTrackClient`, and records the audit event through another client command. Persistent
steps can fail after the password has changed, leaving the force-change flag, PAT set, and audit
trail inconsistent with the credential.

Cookie refresh is correctly a host responsibility after a committed credential transition. The
persistent password/hash, force-change flag, security/concurrency stamps, PAT revocation, and
required audit event are one library operation.

**Remediation:**

- Add `ChangeOwnPasswordAsync` to `IAccountCredentialCommands`.
- Verify the current password through the injected Identity password hasher, then update the
  identity row, clear `RequiresPasswordChange`, rotate stamps, revoke PATs, and write the audit event
  in one provider transaction.
- Return only the state needed to refresh the current sign-in; never return a hash or credential
  material.
- Make the Razor Page validate input, invoke that single command, apply the returned stamp state,
  refresh the cookie, and redirect.
- Keep login-success/failure telemetry outside this transaction: those events describe
  authentication attempts, not a compound credential mutation.

**TDD evidence required:**

1. Shared credential-port tests prove incorrect-current-password rejection and rollback at every
   persistent step.
2. Provider tests prove password update, flag clear, stamp rotation, PAT revocation, and audit are
   committed together.
3. Web integration tests prove command failure leaves the old password usable and PATs unchanged,
   and success refreshes the current cookie only after commit.

### 2.3 Internal application SPIs are part of the public compatibility surface

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Library API / encapsulation |
| **Files** | `src/JobTrack.Application/Ports/*.cs`, concrete `*Commands.cs`/query classes, `PublicAPI.*.txt` |

The documented entry point is `IJobTrackClient`, but all persistence ports and the concrete command
handler types are public. Consumers can bypass the configured facade, construct incomplete command
graphs, or implement an SPI whose authorization/transaction obligations are easy to miss.
`InternalsVisibleTo` is already configured for both providers, so the assembly arrangement indicates
that these types were intended to be implementation details.

Because the library gate treats the current API as a compatibility commitment, this must be an
explicit API decision rather than an opportunistic visibility edit.

**Remediation:**

- Perform an FDG/public-API review and record an ADR choosing one of:
  - internalize ports, port-only records, and concrete handlers, keeping request/result contracts
    and facade interfaces public; or
  - define and document a supported provider-extension SPI, including authorization, transaction,
    audit, and conformance obligations.
- Prefer internalization unless third-party persistence providers are a real supported product
  requirement.
- If internalizing, add only the narrow friend assemblies required by providers and tests, update
  `PublicAPI.*.txt`, and record the deliberate pre-release breaking change.
- Add a public-surface architecture test that exports only approved facade contracts and provider
  factories.

### 2.4 Expressible conditional updates use duplicated inline SQL

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Persistence / house style |
| **Files** | both job-node and work-session command ports; other `ExecuteSqlInterpolatedAsync` call sites |

Explicit pickup and automatic session-start claim repeat an inline
`UPDATE job_node ... WHERE owner_user_id IS NULL` in four places. This is expressible with EF
`ExecuteUpdateAsync`, conflicts with the EF-first rule, and duplicates the concurrency primitive
whose semantics must remain identical.

Not every raw SQL call is wrong: advisory-lock calls and invocations of source-controlled
PostgreSQL functions are provider mechanics. The current code does not keep that distinction
obvious.

**Remediation:**

- Add one internal shared conditional-claim helper over `DbContext`, implemented with
  `ExecuteUpdateAsync`, and use it from explicit pickup and all automatic-claim paths.
- Inventory every runtime raw-SQL call. Convert EF-expressible reads/writes to LINQ/EF.
- Keep irreducible PostgreSQL behaviour only as a source-controlled stored function/procedure
  invoked through EF, and keep connection/pragma or advisory-lock commands in narrowly named
  provider infrastructure.
- Add an architecture test that rejects inline DML in runtime C# outside an explicit, reviewed
  allowlist of provider-mechanism files.

### 2.5 Architecture tests do not enforce one intent/one mutation

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Architecture test coverage |
| **Files** | `tests/JobTrack.ArchitectureTests` |

The current suite checks project references, ASP.NET dependencies, provider namespaces, SQL leakage,
authorization attributes, clocks, and civil time. All pass while a Razor handler coordinates two
library mutations.

**Remediation:**

- Add a syntax-aware architecture test for Razor `OnPost*` handlers and API endpoint delegates:
  one handler may invoke at most one `IJobTrackClient` mutation.
- Queries used to prepare or validate an otherwise single mutation are permitted, though command
  design should still prefer authoritative in-transaction reloads.
- Maintain a small named allowlist only for authentication-attempt audit events and composition
  mechanics; do not allowlist business workflows.
- Add a test that command methods documented as composites are implemented by one command-port call,
  not multiple public command calls.

### 2.6 Documentation and directive drift obscures the real boundary

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | Documentation / house style |
| **Files** | `docs/traceability/test-catalogue.md`, three ownership-filter switches in `JobTrack.Web`, `.editorconfig` and repository directives |

- The traceability catalogue still cites the superseded test
  `A_worker_cannot_start_a_session_on_an_unassigned_leaf` while a later row correctly records ADR
  0048's opposite behaviour.
- Three web ownership-filter switches use the forbidden empty property binding pattern
  `(false, { } value)`.
- The repository directive says Allman braces, while `.editorconfig` deliberately omits
  `control_blocks` from `csharp_new_line_before_open_brace`; formatter output therefore uses
  same-line braces for control flow. The directive also says not to edit the shared
  `.editorconfig`, so this conflict needs an explicit owner decision.

**Remediation:**

- Replace the stale catalogue evidence with the ADR 0048 test names and keep one authoritative row.
- Replace the three empty property bindings with nullable value type patterns such as
  `(false, long ownerUserId)`.
- Decide whether Allman applies to control blocks. If yes, approve a dedicated formatting-policy
  change despite the normal `.editorconfig` restriction and apply it mechanically in a separately
  reviewed commit. If no, amend the repository directive to match formatter-enforced style. Do not
  churn braces as part of the functional remediation commits.

## 3. Implementation order

Follow TDD and the mandatory database → library → API/web ordering.

### Stage 1 — Atomic work-ending commands

1. Add failing shared contract tests for write-up plus finish/complete atomicity and rollback.
2. Add request/result contracts and provider implementations.
3. Add provider concurrency tests.
4. Adapt the HTTP API and then Razor Page handlers to one command per intent.
5. Remove `SaveWriteUpFirstAsync`; retain the standalone write-up handler.

### Stage 2 — Atomic self-service password transition

1. Add failing credential-port and application-command tests.
2. Implement the transaction in both providers.
3. Replace the page's `UserManager`/token/audit sequence with the single command.
4. Add web integration tests for success and injected failure.

### Stage 3 — Public surface and persistence primitives

1. Record the SPI/public-API decision.
2. Internalize implementation types or document the supported SPI.
3. Introduce the EF conditional-claim helper and migrate all claim call sites.
4. Inventory and remediate remaining runtime inline SQL.

### Stage 4 — Guardrails and documentation

1. Add the one-handler/one-mutation architecture test.
2. Tighten public-surface and inline-DML architecture tests.
3. Correct traceability and forbidden-pattern drift.
4. Resolve the brace-formatting directive conflict in a separate decision/commit.

## 4. Completion criteria

This plan is complete when:

- no Razor Page or API handler coordinates a compound JobTrack business mutation;
- work-ending write-up changes and self-service password changes are each one provider transaction,
  one correlation id, and one commit;
- rollback and contention tests pass for PostgreSQL and SQLite;
- the supported public surface matches the documented facade/SPI decision;
- EF-expressible conditional writes contain no inline SQL;
- architecture tests prevent regression of these boundaries;
- `docs/traceability/test-catalogue.md` reflects ADR 0048 without contradictory evidence;
- the commit gate passes for every slice, targeted provider/web tests pass, and the full solution
  suite passes once at plan completion.

## 5. Review-time evidence

The review ran:

- `gtimeout 60 dotnet test tests/JobTrack.ArchitectureTests/JobTrack.ArchitectureTests.csproj --no-restore`
  — 24 passed;
- `gtimeout 180 dotnet test tests/JobTrack.Persistence.PostgreSql.Tests/JobTrack.Persistence.PostgreSql.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkSessionCommandPortTests"`
  — 60 passed;
- `gtimeout 180 dotnet test tests/JobTrack.Persistence.Sqlite.Tests/JobTrack.Persistence.Sqlite.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkSessionCommandPortTests"`
  — 59 passed.

These results verify the existing session auto-claim implementation and show that the findings are
boundary/coverage gaps rather than failures in ADR 0048's tested behaviour.
