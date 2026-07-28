# Fresh-Eyes Code Review Remediation Plan

**Date:** 2026-07-28  
**Status:** Proposed  
**Scope:** Current `main` at `ee903143`, with emphasis on work added after the 2026-07-25
scalability follow-up: prerequisite regression/reopening, Awaiting Progress readiness ordering,
performance-gate changes, and remembered web filters.

This plan records only findings independently confirmed against the current source, accepted ADRs,
and tests. It does not reopen implemented findings from the 2026-07-19, 2026-07-23, 2026-07-24,
2026-07-25, or 2026-07-26 plans. It also does not absorb the separate proposed multi-instance
deployment or PostgreSQL column-type plans.

## 1. Review baseline

The worktree was clean at review start. The following checks passed on 2026-07-28:

- `gtimeout 300 dotnet build JobTrack.slnx -warnaserror` — 0 warnings, 0 errors.
- `gtimeout 300 ./scripts/fast-test.sh --build` — 1,418 tests passed.

The fast suite took 24 seconds against its documented 20-second budget and emitted only a warning.
That is itself finding §2.4; it does not invalidate the correctness baseline.

Static checks also found no current production violations of the empty-braces null-pattern ban,
Razor `DateTimeOffset` instant binding rule, direct runtime wall-clock rule, or postfix-increment
rule.

## 2. Findings

Severity: **High** > **Medium** > **Low**.

### 2.1 `SetAchievementAsync` can reopen a prerequisite without taking the PostgreSQL readiness lock

| | |
|---|---|
| **Severity** | **High** |
| **Category** | Database concurrency / domain correctness |
| **Files** | `PostgreSqlAchievementCommandPort.cs`, `PrerequisiteReadinessSerialization.cs`, provider race tests |
| **Authority** | ADR 0051; implementation plan §14.1 |

ADR 0051 requires a reopen and a concurrent dependent readiness decision to be ordered. The
dependent may start or close only if it committed while the prerequisite was still `Success`.
`ReopenAndStartWorkAsync` enforces that on PostgreSQL by passing its own node id as
`additionallyLockedRequiredJobId` to `LeafReadiness.IsReadyAsync`. A dependent start/completion takes
the same advisory lock for that required job.

The other supported reopen route does not. `PostgreSqlAchievementCommandPort.SetAchievementAsync`
computes `isReopening`, but when the requested state is `Waiting` it never calls
`LeafReadiness.IsReadyAsync` and never directly calls
`PrerequisiteReadinessSerialization.AcquireAsync`. Its only writes are to the prerequisite's
`leaf_work` and audit rows; the dependent command writes a different leaf. Optimistic row versions
therefore cannot serialize the two operations.

A possible execution is:

1. the prerequisite command reads `Success`;
2. the dependent command acquires the prerequisite advisory lock and reads `Success`;
3. the prerequisite command writes `Waiting` without waiting for that lock;
4. both commands commit changes derived from snapshots that did not contain the other's change.

The final state may resemble a legal “dependent acted, then prerequisite reopened” state, but the
dependent did not necessarily commit while the prerequisite remained successful. This is exactly
the snapshot race ADR 0051 says the retained serialization prevents. Existing races cover
`ReopenAndStartWorkAsync` versus dependent start, not `SetAchievementAsync` versus a dependent start
or completion.

#### Remediation

1. **Write the failing PostgreSQL race first.**
   - Seed a successful required leaf and a ready dependent.
   - Coordinate independent connections so `SetAchievementAsync(Success -> Waiting)` and a
     dependent start or terminal transition overlap after reading their initial rows.
   - Assert an ordered outcome: if reopen obtains the lock first, the dependent receives
     `PrerequisiteBlockedException`; if the dependent obtains it first and commits, reopen may then
     succeed.
   - Assert audit rows and row versions match the winning order; no partial session or achievement
     write may survive a rejected command.
2. Add the SQLite counterpart as provider-contract evidence. SQLite's writer transaction should
   already serialize the operations, but the test must prove the public outcome rather than relying
   on that assumption.
3. In `PostgreSqlAchievementCommandPort.SetAchievementAsync`, take the canonical required-job lock
   when a transition leaves `Achievement.Success`. Acquire it before changing `leaf_work`, using the
   same helper and key as every readiness-gated dependent command. Do not introduce a second lock-key
   scheme.
4. Keep the lock narrowly scoped. Reopening `Cancelled` or `Unsuccessful` cannot invalidate
   readiness because those states never satisfied a prerequisite; do not serialize them without
   evidence.
5. Re-run the affected achievement/work-session contract suites and provider races under explicit
   timeouts.

#### Acceptance

- Every command that can change a required job from `Success` to non-success participates in the
  same PostgreSQL advisory-lock protocol as dependent starts and terminal transitions.
- The two race orderings are deterministic and tested on independent connections.
- A rejected dependent action leaves no session, achievement, or audit residue.

---

### 2.2 `job_node_blocked()` repeats recursive achievement work per prerequisite edge

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Database query scalability / provider parity |
| **Files** | `database/postgresql/schema-versions/0013_hierarchy-achievement-and-readiness-queries.sql`, `SqliteAwaitingProgressQueryPort.cs`, performance tests |
| **Authority** | ADR 0052; implementation plan §6.5 and §14.1 |

ADR 0052 and the PostgreSQL function comment say the blocked-set query resolves each distinct
required job once, then descends from each blocking declaration. The implementation does not:

```sql
SELECT jp.to_id
FROM job_prerequisite jp
WHERE NOT node_succeeded(jp.from_id)
```

`node_succeeded` recursively traverses the required subtree. PostgreSQL evaluates that expression
from prerequisite-edge rows, so one required branch shared by many dependents can repeat the same
recursive traversal many times. SQLite already uses the intended shape: a distinct `required` CTE,
one `unsatisfied` result per required id, then a join back to prerequisite declarations.

Current performance fixtures contain roughly one forward edge per adjacent leaf. They do not cover
the high-fan-out shape that exposes this defect. The Awaiting Progress performance row also has no
formal budget yet, and the new blocked relation is computed even when `ExcludeBlocked` is false
because readiness is now the first ordering key.

#### Remediation

1. Add a PostgreSQL performance regression fixture with:
   - one non-trivial required branch;
   - thousands of dependent declarations sharing that branch;
   - a realistic mix of finished and unfinished candidate leaves; and
   - both `ExcludeBlocked = false` and `true`.
2. Capture `EXPLAIN (ANALYZE, BUFFERS)` for the current query. Record execution count/loops and total
   time in `docs/traceability/performance-budgets.md`; do not select a new ceiling before measuring
   the isolated warm baseline.
3. Edit pre-release schema version `0013` in place. Mirror SQLite's relational shape:
   `required(DISTINCT from_id)` → `unsatisfied(required id)` → declarations → recursive blocked
   descendants.
4. Add a schema/contract test with many declarations sharing one required job. Correctness tests
   must still cover a required branch, inherited prerequisites, a satisfied required job, and
   duplicate paths converging on one descendant.
5. Re-measure. If the distinct-required rewrite is still installation-wide and material at the
   accepted scale, perform a second design step to scope blocked computation to the already filtered
   Awaiting Progress candidate relation. Do not add a maintained readiness cache.
6. Verify PostgreSQL and SQLite return identical ordered pages and exclusion results.

#### Acceptance

- Recursive achievement is evaluated once per distinct required job, not once per edge.
- The prerequisite-fan-out fixture has an explicit, evidence-backed warm budget.
- Blocked filtering and readiness-first paging remain equivalent on both providers.

---

### 2.3 Performance regression ceilings are being widened to absorb cross-project contention

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Test-gate integrity |
| **Files** | `FullTableHierarchyLoadPerformanceTests.cs`, performance test runner configuration, `performance-budgets.md` |

Commit `3cae1b87` widened two Awaiting Progress ceilings solely because the full solution runs
PostgreSQL-heavy projects concurrently against one local instance:

- an isolated/default-page measurement of about 34 ms is guarded at 1,500 ms;
- a 744–1,285 ms isolated combined-tree measurement is guarded at 2,500 ms.

The comments correctly identify environmental contention, but raising product-regression ceilings
to accommodate that environment reduces their ability to detect a real regression. A 34 ms query
can now become more than forty times slower without failing its named guard. Repeating this policy
as the suite grows makes the gate progressively less meaningful.

#### Remediation

1. First add a reproducible runner-level test/CI experiment that executes the performance project:
   - alone against a cleaned PostgreSQL instance;
   - during the current parallel full-solution run; and
   - with PostgreSQL-backed test projects serialized.
2. Choose one deterministic performance lane. Prefer a dedicated, serialized invocation of
   `JobTrack.Database.PerformanceTests` after correctness suites, with database cleanup before and
   after it. The ordinary full-solution run may still compile the project, but must not be the source
   of latency ceilings if unrelated projects contend for the same server.
3. Restore query ceilings from isolated warm measurements plus documented headroom. Keep cold-start
   and contended observations as diagnostics, not pass/fail budgets for query code.
4. Record machine assumptions, warm-up protocol, PostgreSQL configuration, scale seed, and the exact
   command beside every formal budget.
5. Make future ceiling increases require a before/after query plan and an explicit explanation of
   the product regression or changed supported scale. “The shared test server was busy” is a runner
   defect, not a reason to relax a query budget.

#### Acceptance

- Isolated performance gates pass repeatably without depending on solution-project scheduling.
- A deliberate 2× slowdown of the realistic default-page query fails the guard.
- The full correctness suite no longer causes performance-ceiling churn.

---

### 2.4 The fast-suite budget is breached and is not a gate

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Developer feedback / commit-gate reliability |
| **Files** | `scripts/fast-test.sh`, `README.md`, repository operating guidance |

The required `./scripts/fast-test.sh --build` run took 24 seconds against a 20-second budget. The
script prints a warning but exits successfully, so “Sub-20s” is descriptive text rather than an
enforced gate. With `--build`, it invokes `dotnet test` separately for seven projects and allows
each invocation to restore/build even though the commit gate has just built the entire solution.
The reported test execution itself was about 11 seconds; repeated CLI/MSBuild startup and
restore/build checks account for much of the remaining wall time.

#### Remediation

1. Add a script-level regression harness before changing behaviour. Inject the command runner and
   elapsed-time source so tests can prove:
   - every intended fast project is run exactly once;
   - every invocation is bounded by `gtimeout`;
   - a build failure or test failure is propagated; and
   - the selected enforcement mode fails when its budget is exceeded.
2. Change `--build` to perform at most one build, then run tests with `--no-build --no-restore`, or
   introduce a small fast-suite solution/filter that lets the SDK schedule the selected assemblies
   without seven redundant build evaluations.
3. Re-measure on the supported development environment. Retain the 20-second budget if the redundant
   work was the cause; otherwise revise the budget once with evidence and update every authoritative
   reference together.
4. Make budget enforcement explicit and deterministic. A local informational mode and a CI
   enforcement mode are acceptable, but the documented commit gate must say which one is
   authoritative.

#### Acceptance

- The fast suite meets its documented budget on repeated warm runs.
- The authoritative budget mode exits non-zero on a real overrun.
- No test project silently falls out of the fast-suite membership.

---

### 2.5 Remembered filters survive logout and can cross account boundaries

| | |
|---|---|
| **Severity** | **Medium** |
| **Category** | Web session-state correctness / privacy |
| **Files** | `Pages/Account/Login.cshtml.cs`, `Pages/Account/Logout.cshtml.cs`, `FilterMemory.cs`, account-flow tests |

Awaiting Progress now stores owner, unassigned, subtree, whole-tree, search text, and blocked
visibility in `HttpContext.Session`. `LogoutModel.OnPostAsync` signs out the Identity cookie but does
not clear session state. The session cookie has the same eight-hour lifetime as authentication.

Consequently, another employee signing in through the same browser session can inherit the previous
employee's owner/subtree selection and free-text search. The job tree is intentionally broadly
browsable, so this is not an authorization bypass, but it is cross-principal state leakage and can
show one employee another's search text. It also defeats the home-node default for the second user.
An expired authentication cookie followed by a different login has the same issue even without an
explicit logout.

#### Remediation

1. Write an integration test with two synthetic employees and one cookie container:
   - employee A stores every Awaiting Progress filter, including a distinctive search term;
   - A logs out and employee B signs in;
   - B's bare visit uses B's own home-node/default filters and never renders A's search term.
2. Centralize principal-bound browser-state reset. Clear JobTrack session state on successful final
   authentication and on logout. Cover ordinary login and final 2FA completion; do not clear state
   between the password and 2FA steps.
3. Keep the existing recently-visited `localStorage` logout cleanup aligned with the server-side
   reset and test both in the browser suite.
4. Do not solve this by putting user ids into session keys indefinitely; all principal-bound state
   should have one reset boundary so future remembered pages cannot miss it.

#### Acceptance

- No remembered server- or browser-side workflow state crosses from one authenticated principal to
  another in the same browser profile.
- A normal return visit by the same signed-in employee still recalls filters.

---

### 2.6 Test constant tables still use mutable `static readonly T[]`

| | |
|---|---|
| **Severity** | **Low** |
| **Category** | House style / guardrail integrity |
| **Files** | Architecture and web integration tests |

The 2026-07-26 FDG audit replaced four production constant arrays, but the repository still has 18
private `static readonly T[]` tables under `tests/`. Most are security or architecture allowlists,
expected route contracts, and forbidden-token tables. `readonly` protects only the array reference;
the elements remain mutable. These are exactly the constant-table shape prohibited by the current
house style, and mutation of an allowlist can weaken the guardrail that is meant to police
production code.

#### Remediation

1. Add a failing architecture test that scans `src`, `tests`, and `samples` for constant
   `static readonly T[]` declarations. The guard must not use that same representation internally.
2. Convert membership-only tables to `FrozenSet<T>`.
3. Convert ordered tables to `static ReadOnlySpan<T>` properties where consumers accept spans;
   otherwise use an immutable/read-only collection appropriate to the consuming API. Do not hide a
   mutable array behind `IReadOnlyList<T>`.
4. Review the nearby mutable `static readonly HashSet<T>` and `Dictionary<K,V>` architecture
   allowlists at the same time. Convert them to frozen collections where construction-time mutation
   is not required.

#### Acceptance

- No constant table in `src`, `tests`, or `samples` is backed by an externally mutable array or
  collection.
- Architecture/security tests retain the same allowlist contents and continue to fail when a
  forbidden production construct is introduced.

## 3. Implementation sequence

The order keeps earlier-layer defects ahead of host/test-harness cleanup and gives each stage a
separate reviewable commit.

| Stage | TDD slice | Suggested commit |
|---|---|---|
| 1 | §2.1 failing cross-connection races, PostgreSQL lock fix, SQLite parity evidence | `fix(persistence): serialize achievement reopens with dependent readiness` |
| 2 | §2.2 fan-out fixture, schema-0013 rewrite, dual-provider contract and measured plan | `perf(database): resolve blocked prerequisites once per required job` |
| 3 | §2.3 deterministic performance lane and restored evidence-backed ceilings | `test(performance): isolate PostgreSQL latency budgets` |
| 4 | §2.4 fast-suite harness, single-build execution, enforced authoritative budget | `build(test): make the fast-suite budget enforceable` |
| 5 | §2.5 cross-account failing test and authentication-bound state reset | `fix(web): clear remembered workflow state across sign-ins` |
| 6 | §2.6 failing architecture rule and immutable test tables | `refactor(test): make guardrail constant tables immutable` |

Each commit message must include the required explanatory paragraph after its conventional summary.
Do not combine the schema/query correction with web session cleanup.

## 4. Verification and completion evidence

Run the repository commit gate after each stage:

```bash
gtimeout 300 dotnet build JobTrack.slnx -warnaserror
dotnet format JobTrack.slnx
gtimeout 300 ./scripts/fast-test.sh --build
```

Add the following targeted runs as their stages land:

```bash
gtimeout 300 dotnet test tests/JobTrack.Persistence.PostgreSql.Tests \
  --filter "FullyQualifiedName~AchievementCommandPortTests|FullyQualifiedName~WorkSessionCommandPortTests"

gtimeout 300 dotnet test tests/JobTrack.Persistence.Sqlite.Tests \
  --filter "FullyQualifiedName~AchievementCommandPortTests|FullyQualifiedName~WorkSessionCommandPortTests|FullyQualifiedName~AwaitingProgressQueryPortTests"

gtimeout 300 dotnet test tests/JobTrack.Database.ContractTests \
  --filter "FullyQualifiedName~HierarchyAchievementReadiness"

gtimeout 600 dotnet test tests/JobTrack.Database.PerformanceTests \
  --filter "FullyQualifiedName~FullTableHierarchyLoadPerformanceTests"

gtimeout 300 dotnet test tests/JobTrack.Web.IntegrationTests \
  --filter "FullyQualifiedName~AccountFlowTests|FullyQualifiedName~AwaitingProgressTests"

gtimeout 120 dotnet test tests/JobTrack.ArchitectureTests
```

Run `./scripts/clean-test-databases.sh` after any interrupted database test and after the performance
lane. Run the full solution suite once after all six stages, using the new deterministic performance
protocol rather than widening ceilings in response to shared-server contention.

Before changing this plan to `Implemented`:

1. record the commit for each stage;
2. record the isolated before/after `job_node_blocked()` plan and latency;
3. record both race orderings and their final states;
4. record the fast-suite elapsed time and enforcement mode;
5. confirm the plan index status matches this file; and
6. confirm no finding was merely documented or suppressed.
