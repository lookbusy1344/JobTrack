# Framework Design Guidelines Follow-up Remediation Plan

**Date:** 2026-08-07
**Status:** Implemented (2026-08-07)
**Scope:** Close the agreed findings from the 2026-08-07 fresh-eyes review of the reusable .NET
surface against `../Framework_Design_Guidelines_Essentials.md` and
`../Framework_Design_Guidelines_Modern_Supplement.md`: freeze the accepted 1.0 API baseline,
replace public Boolean parameter clusters with explicit immutable fact contracts, add simple
provider-factory overloads, and bring the consumer-first API design document back into agreement
with the shipped surface.

**Owner decisions incorporated (2026-08-07):** public interfaces are an accepted design choice;
`EquatableDictionary<TKey, TValue>.GetEnumerator()` remains unchanged; no second-language consumer
sample is required; `TryAuthenticateAsync` remains unchanged because expected-absence `Try*`
members are desirable here, and only its stale design documentation is corrected.

## 1. Context and authority

ADR 0013 makes the public surfaces of `JobTrack.Abstractions`, `JobTrack.Domain`,
`JobTrack.Application`, `JobTrack.Persistence.PostgreSql`, and `JobTrack.Persistence.Sqlite`
compatibility commitments when the M6 library gate passes. It defines that gate acceptance as the
project's internal "1.0" boundary and says the reviewed contents of `PublicAPI.Unshipped.txt` move
to `PublicAPI.Shipped.txt` once at that boundary. ADR 0026 accepted M6, and ADR 0063 subsequently
accepted the 1.0 release gate, but that mechanical promotion did not occur: every shipped file
still contains only `#nullable enable`, while the complete approved surface remains unshipped.

The current build is clean because `Microsoft.CodeAnalysis.PublicApiAnalyzers` verifies that the
surface is listed; it does not treat an entry that has never been promoted to `Shipped` as an
immutable compatibility commitment. The public API and architecture suites likewise pass because
they test composition, behavior, and boundary reachability rather than the lifecycle state of the
baseline files. This is a release bookkeeping and enforcement defect, not evidence that those
tests are wrong.

The same review found public authorization-policy methods with two to five Boolean parameters,
despite the FDG member-design rule and `docs/api/jobtrack-client-design.md` both rejecting Boolean
clusters. These methods are pure and well tested; the defect is the public call shape, not their
authorization behavior. The remediation must therefore preserve every existing truth table and
both providers' effects exactly.

Finally, each provider factory has a longest overload with three optional parameters but no
explicit simple overload. The common calls compile because optional arguments fill the gap, but
the FDG requires a no-default simple overload when two or more defaults exist. Adding those
overloads is additive and does not change existing binding behavior.

House rules apply throughout: TDD for product/API changes, no test whose subject is the test
harness, no weakening or deletion of existing tests, one public type per file, the normal commit
gate after each implementation slice, and a full `./scripts/all-test.sh` run only at final
close-out.

## 2. Scope and non-goals

### 2.1 In scope

1. Promote the accepted public surface from `PublicAPI.Unshipped.txt` to
   `PublicAPI.Shipped.txt` in all six analyzer-enabled library/provider projects, including
   `JobTrack.Persistence.Shared`'s intentionally empty surface.
2. Add explicit immutable fact contracts for requester and reopen-and-start authorization, and
   replace the Boolean-cluster public method signatures with those contracts.
3. Update every in-repo caller atomically with the signature change: Application, both providers,
   test-support fakes, unit tests, provider contract tests, XML documentation, and public API
   snapshots.
4. Add explicit simple overloads for the PostgreSQL and SQLite composition factories, including
   PostgreSQL's split-PAT-data-source factory.
5. Refresh `docs/api/jobtrack-client-design.md`, the public usage example's stale commentary,
   ADR 0013's lifecycle note, and the earlier FDG plan's now-superseded observations.
6. Re-run the relevant public API, architecture, domain, application, and dual-provider tests and
   record the final evidence in this plan.

### 2.2 Explicit non-goals

- No change from interfaces to classes or abstract classes.
- No change to `EquatableDictionary<TKey, TValue>` or its enumerator.
- No F#, PowerShell, VB, or other second-language sample.
- No rename, new counterpart, return-shape change, or behavioral change for
  `ITokenCommands.TryAuthenticateAsync`.
- No authorization-policy change, role change, requester-visibility change, or work-session
  authority change.
- No HTTP API or Razor Pages contract change. They consume the same library behavior through
  updated in-repo call sites.
- No package versioning or external NuGet publication work; ADR 0013's internal-monorepo policy
  remains authoritative.
- No test added merely to inspect baseline-file placement. The analyzer is the existing executable
  enforcement, and the repository rule expressly forbids tests whose subject is the harness.

## 3. Target API design

### 3.1 Immutable authorization fact contracts

The Boolean inputs are independent facts, not one closed choice, so forcing them into enums would
create invalid or combinatorial enum values. Group them into small nominal records instead. This
keeps the public method signatures readable and makes every call site name each fact.

Add one public sealed nominal record per file under `JobTrack.Domain.Authorization`:

```csharp
public sealed record RequesterSubmissionFacts
{
    public required bool IsHoldingAreaActive { get; init; }
    public required bool ActorIsEligibleForHoldingArea { get; init; }
}

public sealed record RequesterVisibilityFacts
{
    public required bool ActorIsRequestOwner { get; init; }
    public required bool IsDepartmentVisibilityEnabled { get; init; }
    public required bool ActorSharesRequestDepartment { get; init; }
    public required bool ActorControlsAnchorNode { get; init; }
}

public sealed record RequesterCommentFacts
{
    public required RequesterVisibilityFacts Visibility { get; init; }
    public required bool IsOpenToRequester { get; init; }
}

public sealed record LeafReopenAndStartFacts
{
    public required bool ActorControlsNode { get; init; }
    public required bool ActorParticipatedPreviously { get; init; }
    public required AppUserId ActorUserId { get; init; }
    public required AppUserId TargetWorkedByUserId { get; init; }
}
```

The exact property names are part of the implementation review. Preserve affirmative Boolean
naming and avoid reintroducing positional Boolean constructors. Nominal records with `required`
`init` properties are intentional: positional records would merely move the Boolean cluster into
a generated public constructor and deconstructor.

Replace the affected policy members with:

```csharp
public static bool CanSubmit(
    IReadOnlyCollection<EmployeeRole> actorRoles,
    RequesterSubmissionFacts facts);

public static bool CanView(
    IReadOnlyCollection<EmployeeRole> actorRoles,
    RequesterVisibilityFacts facts);

public static bool CanCommentAsRequester(
    IReadOnlyCollection<EmployeeRole> actorRoles,
    RequesterCommentFacts facts);

public static bool CanReopenAndStartFor(
    IReadOnlyCollection<EmployeeRole> actorRoles,
    LeafReopenAndStartFacts facts);
```

Each method validates both reference arguments synchronously with
`ArgumentNullException.ThrowIfNull`. The method bodies remain pure. `CanCommentAsRequester`
delegates to `CanView(actorRoles, facts.Visibility)` and then applies the requester-role and
`IsOpenToRequester` conditions. The inversion from the old
`requestIsClosedToRequester` argument is deliberate and must be driven by truth-table tests before
callers change.

These signature replacements are source and binary breaking changes to the newly frozen 1.0
surface. ADR 0013 permits them for this internal monorepo when the break is explicit, every in-repo
consumer changes in the same commit, and the API baseline records the decision. Do not hide the
break with permanent obsolete forwarding overloads: those overloads would leave the Boolean
clusters in the public contract and would not close the finding. Instead, use PublicApiAnalyzers'
supported removed-API annotation in `PublicAPI.Shipped.txt` for the four old signatures and place
the four replacement methods and four new fact types in `PublicAPI.Unshipped.txt`. Do not silently
delete shipped entries or suppress RS0017.

### 3.2 Provider factory overloads

Retain the existing longest overloads and their optional customization parameters. Add explicit
simple overloads that delegate to the longest overload with `null` customizations:

```csharp
public static IJobTrackClient JobTrackSqlite.Create(string connectionString);

public static IJobTrackClient JobTrackPostgreSql.Create(NpgsqlDataSource dataSource);

public static IJobTrackClient JobTrackPostgreSql.CreateWithPatDataSources(
    NpgsqlDataSource dataSource,
    NpgsqlDataSource personalAccessTokenManagementDataSource,
    NpgsqlDataSource personalAccessTokenAuthenticationDataSource);
```

The longest overloads remain the sole implementation point. The simple overloads perform no
separate validation; delegation reaches the same synchronous validation once. Keep
`[CLSCompliant(false)]` on every overload whose dependency types require it and document the
simple/advanced relationship with `<summary>`/`<remarks>`/`<inheritdoc>` as appropriate.

The overloads are additive. Record them in each provider's `PublicAPI.Unshipped.txt` after the 1.0
promotion; do not edit the shipped baseline for additive members.

### 3.3 Documentation truth

`docs/api/jobtrack-client-design.md` remains the consumer-first design artefact, but it must
describe the actual accepted surface rather than the early incremental state. Update it to:

- list every current `IJobTrackClient` property with its exact name (`Costs`, not `Costing`) and
  status;
- describe the fact-record approach instead of claiming no Boolean inputs exist anywhere;
- describe `TryAuthenticateAsync` as the deliberate expected-absence authentication operation,
  without proposing a throwing counterpart or shape change;
- remove the assertion that no real persistence-backed implementation exists;
- describe the simple and advanced provider factory overloads;
- keep source links and examples aligned with the actual code; and
- distinguish historical design sequencing from current 1.0 state where that history remains
  useful.

Update ADR 0013 with a dated implementation note that M6/1.0 has passed and the promotion was
completed by this plan. Do not rewrite the original decision as if the omission never happened.
Likewise, annotate the relevant observations in
`2026-07-26-framework-design-guidelines-compliance-plan.md` as superseded rather than deleting
historical review evidence.

## 4. Ordered implementation stages

### Stage 1 — Freeze the accepted 1.0 baseline

This stage must land before any API shape changes so the repository records the exact surface that
M6 and the release gate accepted.

1. For each of `JobTrack.Abstractions`, `JobTrack.Domain`, `JobTrack.Application`,
   `JobTrack.Persistence.Shared`, `JobTrack.Persistence.PostgreSql`, and
   `JobTrack.Persistence.Sqlite`, move every current API entry below `#nullable enable` from
   `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` without regenerating, reordering, adding,
   or deleting entries.
2. Leave each `PublicAPI.Unshipped.txt` containing only `#nullable enable`.
3. Review the diff mechanically: the multiset of entries across each shipped/unshipped pair must
   be unchanged; only lifecycle placement changes.
4. Add a dated implementation note to ADR 0013 stating that the promotion required by its
   consequence section is now complete.
5. Do not add a baseline-placement test. Run the analyzer-bearing build; it is the applicable
   executable check.

**Acceptance:** `dotnet build JobTrack.slnx -warnaserror` passes; all six shipped files contain the
accepted surface; all six unshipped files contain only the nullable header; `git diff` shows no
public source-code change.

**Commit scope:** baseline files plus the ADR 0013 implementation note only. Suggested subject:
`build(api): freeze the accepted 1.0 public surface`, followed by a paragraph explaining the
missed M6 promotion and the unchanged union of API entries.

### Stage 2 — Replace Boolean-cluster authorization signatures

TDD order is mandatory because the semantic risk is in reconstructing named facts at every caller.

1. Rewrite `RequesterAccessPolicyTests` and `LeafReopenAndStartAccessPolicyTests` first to use the
   proposed fact records. Preserve every current truth-table case; add focused tests for:
   - null fact records throwing `ArgumentNullException` with `facts` as `ParamName`;
   - `RequesterCommentFacts.IsOpenToRequester = false` denying a comment;
   - the same visibility facts producing the same `CanView` result when nested in comment facts;
   - default/unspecified user identifiers retaining current equality-based behavior rather than
     acquiring an invented validation rule; and
   - all property combinations currently represented by positional Boolean calls.
2. Run the targeted Domain tests and observe the expected compile failure because the fact types
   and signatures do not yet exist. That compile failure is the red TDD state; do not weaken the
   tests back to the old signatures.
3. Add the four nominal records, XML documentation, and replacement policy signatures with the
   smallest body changes required to make the Domain tests pass.
4. Update all in-repo callers in one slice:
   - PostgreSQL and SQLite job-request command ports;
   - PostgreSQL and SQLite work-session command ports;
   - `JobQueries` and any other Application query projection;
   - `FakeWorkSessionCommandPort` and other test-support callers; and
   - direct policy unit tests.
5. Run the requester and work-session provider contract classes on both providers. Existing tests
   must remain unchanged except where constructing the new public fact contracts is the test's
   direct subject.
6. Mark the four old shipped signatures using the analyzer's removed-API mechanism. Add the new
   types, generated record surface, and replacement signatures to
   `JobTrack.Domain/PublicAPI.Unshipped.txt`. Review every generated record member deliberately.
7. Record the source/binary break explicitly in this plan's status/evidence section when the stage
   closes, satisfying ADR 0013's breaking-change process for the in-repo consumer set.

**Targeted verification:** use `gtimeout` around every invocation.

```bash
dotnet test tests/JobTrack.Domain.Tests \
  --filter "FullyQualifiedName~RequesterAccessPolicyTests|FullyQualifiedName~LeafReopenAndStartAccessPolicyTests"
dotnet test tests/JobTrack.Persistence.PostgreSql.Tests \
  --filter "FullyQualifiedName~PostgreSqlJobRequestCommandPortTests|FullyQualifiedName~PostgreSqlWorkSessionCommandPortTests"
dotnet test tests/JobTrack.Persistence.Sqlite.Tests \
  --filter "FullyQualifiedName~SqliteJobRequestCommandPortTests|FullyQualifiedName~SqliteWorkSessionCommandPortTests"
dotnet test tests/JobTrack.Application.Tests \
  --filter "FullyQualifiedName~JobQueriesTests"
```

**Acceptance:** the Domain truth tables and both provider contract slices pass; behavior and
exception categories are unchanged; no affected public policy method has more than one Boolean
parameter; PublicApiAnalyzers accepts the explicitly recorded break without suppression.

**Commit scope:** fact contracts, policy methods, every in-repo caller, direct/contract tests, XML
docs, and the Domain API baseline. Suggested subject:
`refactor(auth): replace boolean policy clusters with fact contracts`, followed by a paragraph
identifying the intentional ADR 0013 source/binary break and confirming unchanged authorization
truth tables.

### Stage 3 — Add simple provider factory overloads

1. Add consumer-shaped compile tests before implementation by assigning each factory method group
   to an exact delegate:
   - `Func<string, IJobTrackClient>` for SQLite;
   - `Func<NpgsqlDataSource, IJobTrackClient>` for the shared PostgreSQL data source; and
   - an exact three-data-source delegate for PostgreSQL role separation.
   These assignments do not compile against the current optional-only signatures and therefore
   establish the red TDD state without reflection or a test of the test harness.
2. Add the three explicit overloads, each as a one-line delegation to its existing longest
   overload. Do not duplicate composition or validation.
3. Extend the compiling usage examples to invoke the simple overloads. Keep at least one example
   of the advanced PostgreSQL split-data-source overload so both audience layers remain visible.
4. Add the new signatures to the two provider `PublicAPI.Unshipped.txt` files and review overload
   ambiguity using both compilation and the usage examples.

**Targeted verification:** use `gtimeout` around the invocation.

```bash
dotnet test tests/JobTrack.PublicApi.Tests \
  --filter "FullyQualifiedName~JobTrackClientUsageExampleTests"
```

**Acceptance:** all three exact delegate conversions compile; existing one-argument calls bind to
the new simple overloads; advanced overload calls remain unambiguous; public API and architecture
tests pass.

**Commit scope:** provider factories, compiling public-API examples, XML docs, and provider
unshipped API entries. Suggested subject:
`feat(api): add simple provider factory overloads`, followed by a paragraph explaining that the
longest overloads remain the single implementation path.

### Stage 4 — Refresh the consumer-first design and historical notes

This is documentation work, so no artificial product test is added. The existing compiling usage
examples are the executable companion.

1. Audit `docs/api/jobtrack-client-design.md` from top to bottom against
   `IJobTrackClient`, every public sub-interface, the provider factories, and the API snapshots.
2. Apply §3.3's required corrections, including the owner-approved `TryAuthenticateAsync`
   position.
3. Remove stale "not implemented" wording from
   `JobTrackClientUsageExampleTests` and any directly related public-API commentary while
   preserving useful historical context in the design document.
4. Annotate the 2026-07-26 FDG plan's Boolean-cluster and empty-shipped-file observations as
   superseded by this plan. Do not change that plan's implemented status or erase its original
   findings.
5. Update this plan's status block and the plans index together.

**Acceptance:** every facade property and factory signature named in the design document matches
source; it no longer claims an absence of `Try*` members, providers, or Boolean-related facts; all
links resolve to existing files.

**Commit scope:** documentation and stale source comments only. Suggested subject:
`docs(api): align the client design with the 1.0 surface`, followed by a paragraph listing the
corrected lifecycle, fact-contract, provider, and expected-absence documentation.

### Stage 5 — Final gate and close-out

1. Run the mandatory commit gate from `JobTrack/`:

   ```bash
   dotnet build JobTrack.slnx -warnaserror
   dotnet format JobTrack.slnx
   ./scripts/fast-test.sh --build
   ```

2. Re-run the Stage 2 and Stage 3 targeted filters after formatting.
3. Run `./scripts/all-test.sh` once because this is the close of a multi-stage plan and the
   signature change crosses Domain, Application, both providers, Web consumers, and test support.
4. Review all six shipped/unshipped pairs:
   - the accepted 1.0 surface is in `Shipped`;
   - the four deliberate removals remain visibly recorded;
   - replacement fact contracts and factory overloads are in `Unshipped`; and
   - no unrelated public surface entered either file.
5. Review `git diff --check`, `git status --short`, and the complete diff for unrelated changes.
6. Mark this plan `Implemented (YYYY-MM-DD)` and update its plans-index row in the same close-out
   commit. Record test counts and the full-suite result in §6.

**Acceptance:** the commit gate, targeted suites, public API suite, architecture suite, and final
full suite all pass; API lifecycle files express the intended 1.0 baseline and reviewed changes;
the documentation matches source.

## 5. Risks and controls

| Risk | Control |
|---|---|
| Promoting a regenerated rather than accepted API surface | Move existing entries mechanically and verify the union of each shipped/unshipped pair is unchanged before any source edit. |
| Hiding a post-1.0 breaking change | Freeze first; annotate removed signatures using PublicApiAnalyzers' supported mechanism; name the break in the commit body and this plan. |
| Authorization regression while replacing parameters | Preserve existing truth-table tests first, add the open/closed inversion cases, and run both providers' contract classes. |
| Reintroducing Boolean clusters in constructors | Use nominal records with required init properties, not positional records or multi-Boolean constructors. |
| Fact records grow into mutable bags | Keep them sealed, immutable, narrowly named, and scoped one-per-policy scenario; no setters or persistence behavior. |
| Simple overloads duplicate validation/composition | Delegate directly to the longest overload; keep only one implementation path. |
| New overload ambiguity | Exact delegate conversion tests plus existing compiling consumer examples. |
| Historical documents become misleading or are rewritten | Add dated supersession/implementation notes; retain original accepted decisions and observations. |
| Scope expands into accepted deviations | The non-goals explicitly exclude interfaces, `EquatableDictionary`, other languages, and `TryAuthenticateAsync` code changes. |

## 6. Evidence and completion record

Populate during implementation; a proposed plan is not gate evidence.

| Stage | Status | Evidence |
|---|---|---|
| 1 — Freeze 1.0 baseline | Implemented (2026-08-07) | Mechanical move verified: sorted union of each shipped/unshipped pair unchanged for all six projects (`Abstractions`, `Application`, `Domain`, `Persistence.Shared`, `Persistence.PostgreSql`, `Persistence.Sqlite`). `dotnet build JobTrack.slnx -warnaserror` succeeded, 0 warnings/errors. ADR 0013 carries a dated implementation note. |
| 2 — Authorization fact contracts | Implemented (2026-08-07) | Four nominal records added (`RequesterSubmissionFacts`, `RequesterVisibilityFacts`, `RequesterCommentFacts`, `LeafReopenAndStartFacts`); `RequesterAccessPolicy.CanSubmit/CanView/CanCommentAsRequester` and `LeafReopenAndStartAccessPolicy.CanReopenAndStartFor` now take a fact record instead of Boolean parameters. All in-repo callers updated (Application `JobQueries`, both providers' job-request/work-session command ports, `FakeWorkSessionCommandPort`). `dotnet build JobTrack.slnx -warnaserror` and `dotnet format JobTrack.slnx` both clean. Targeted suites: `JobTrack.Domain.Tests` (`RequesterAccessPolicyTests`+`LeafReopenAndStartAccessPolicyTests`) 37/37 passed; `JobTrack.Persistence.PostgreSql.Tests` (job-request/work-session command port tests) 130/130 passed; `JobTrack.Persistence.Sqlite.Tests` (same) 128/128 passed; `JobTrack.Application.Tests` (`JobQueriesTests`) 137/137 passed. The four old Boolean-cluster signatures are recorded `*REMOVED*` in `JobTrack.Domain/PublicAPI.Unshipped.txt`; the four new fact types and replacement methods are recorded there too — an intentional ADR 0013 source/binary break, every in-repo consumer updated in this same change. |
| 3 — Provider factory overloads | Implemented (2026-08-07) | Added `JobTrackSqlite.Create(string)`, `JobTrackPostgreSql.Create(NpgsqlDataSource)`, and `JobTrackPostgreSql.CreateWithPatDataSources(NpgsqlDataSource, NpgsqlDataSource, NpgsqlDataSource)`, each a one-line delegation to its existing longest overload with `null` customizations. `JobTrackClientUsageExampleTests` gained exact-delegate-conversion and advanced-overload compile/usage tests. `dotnet build JobTrack.slnx -warnaserror` and `dotnet format JobTrack.slnx` both clean; `JobTrack.PublicApi.Tests` (`JobTrackClientUsageExampleTests`) 7/7 passed. New signatures recorded in both providers' `PublicAPI.Unshipped.txt`. |
| 4 — Documentation refresh | Implemented (2026-08-07) | `docs/api/jobtrack-client-design.md`: property table now lists all thirteen `IJobTrackClient` members with exact names (`Costs`, not `Costing`) and status; the Boolean-cluster rule now describes the fact-record approach; `TryAuthenticateAsync` described as the deliberate expected-absence exception, not proposed for a throwing counterpart or shape change; removed the "no persistence-backed implementation" claim (both providers implement the facade); "Registration" replaced with "Provider composition" describing the shipped simple/advanced factory overloads. `JobTrackClientUsageExampleTests`'s stale class-doc corrected. `docs/plans/2026-07-26-framework-design-guidelines-compliance-plan.md`'s Boolean-cluster and empty-shipped-file observations annotated superseded, original findings and implemented status retained. `dotnet build JobTrack.slnx -warnaserror` and `dotnet format JobTrack.slnx` clean; `JobTrackClientUsageExampleTests` 7/7 passed. |
| 5 — Final gate | Implemented (2026-08-07) | `dotnet build JobTrack.slnx -warnaserror` and `dotnet format JobTrack.slnx` clean (0 warnings/errors, no format diffs; a wedged Razor compiler server was cleared with `dotnet build-server shutdown` mid-stage). `./scripts/fast-test.sh --build` passed: 541+438+153+27+25+523+13 = 1,720 tests across the fast core suite, 18s (budget 20s). All Stage 2/3 targeted filters re-passed after formatting (37, 130, 128, 137, 7). `./scripts/all-test.sh` (full solution suite + performance lane) passed: Domain 541, Identity 27, Persistence.Shared 25, Application 438, Persistence.Sqlite 523, PublicApi 13, ArchitectureTests 153, AdminCli.Tests 171, Database.ContractTests 463, Web.EndToEndTests 313, Persistence.PostgreSql.Tests 530, Web.IntegrationTests 592, Database.PerformanceTests 29 — every suite 0 failed. All six `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` pairs reviewed: the accepted 1.0 surface sits in `Shipped`; the four deliberate `*REMOVED*` authorization-method entries, the four new fact-record types, and the three new factory overloads sit in `Unshipped`; no unrelated entries in either. `git diff --check` and `git status --short` clean at close-out. |

This plan is complete only when all five stages are implemented, withdrawn with evidence, or
superseded by an accepted ADR; the plan status and `docs/plans/README.md` must agree.
