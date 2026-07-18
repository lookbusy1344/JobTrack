# Mutation testing gate

**Closes:** Implementation plan §7.5 gate item 4, "mutation testing" — Stryker.NET run against
`JobTrack.Domain`'s four rule-bearing categories named in the plan: interval algebra, authorization,
prerequisite/readiness, and costing.

## What was added

`src/JobTrack.Domain/stryker-config.json` scopes a `dotnet-stryker` run to exactly the files that
carry those four rule categories (not the whole project — value types, exceptions, and DTOs with no
branching logic add nothing to the score and only dilute it):

- `Intervals/IntervalAlgebra.cs`, `Intervals/WorkInterval.cs`, `Schedules/WeeklyInterval.cs` — interval algebra
- `Authorization/*.cs` — authorization
- `Hierarchy/PrerequisiteEdge.cs`, `Hierarchy/UnsatisfiedPrerequisite.cs`, `Hierarchy/ReadinessResult.cs`, `Hierarchy/ReadinessCalculator.cs` — prerequisite/readiness
- `Costing/*.cs`, `Rates/UserCostRate.cs` — costing

`dotnet-stryker` 4.16.0 is a **global** .NET tool (`~/.dotnet/tools/dotnet-stryker`), not a local
tool-manifest entry — there is deliberately no `.config/dotnet-tools.json` in this repo. Run it
directly. The pinned version and the rest of the global toolset are recorded in
[global-tools.md](global-tools.md); the mutation score depends on the Stryker version, so treat a
version bump the same way as the recorded score below.

## Scope: `JobTrack.Domain` only — a deliberate decision

This gate mutates `JobTrack.Domain` and nothing else. That is a conscious choice, not an oversight:

- **Mutation testing earns its cost where branch-dense rule logic lives, and in this library that is
  the pure domain.** The plan (§7.5 item 4) names exactly four rule categories — interval algebra,
  authorization, prerequisite/readiness, and costing — and all four are implemented as pure
  functions in `JobTrack.Domain`. That is where a surviving mutant most plausibly signals a real,
  untested behavioural gap.
- **`JobTrack.Application` is orchestration, not rule logic.** Its command/query handlers load
  authoritative state, authorize inside the transaction, call the domain functions, and emit audit
  intent. Its branches are guard-clause plumbing whose failure modes are already pinned by the 113
  `JobTrack.Application.Tests` (fake-port unit tests) plus the provider **conformance** suites
  running the same commands against real PostgreSQL and SQLite. A mutation run there would mostly
  score guard/null-check equivalents of the shapes already triaged below, at meaningfully higher
  wall-clock cost, without exercising a rule the domain gate does not already cover.
- **The persistence and host projects are excluded for the same reason** — their correctness is
  integrity constraints, EF mapping, and error translation, verified by the database contract and
  provider suites, not by branch coverage a mutant would probe.

**When to revisit.** If application-layer authorization or audit-intent logic grows genuine
branching decisions of its own (beyond delegating to the domain), extend the `mutate` scope to those
specific files — not the whole Application project — and record the added score here.

## Running the gate

```sh
cd src/JobTrack.Domain
dotnet-stryker --config-file stryker-config.json
```

**Must be run from `src/JobTrack.Domain/`.** The config's `test-projects` entry
(`../../tests/JobTrack.Domain.Tests/JobTrack.Domain.Tests.csproj`) and the `mutate` globs are
relative to the current working directory, not the config file's location — Stryker resolves them
against whatever project/solution it auto-detects in the CWD. Running it from the repository root
instead makes it auto-detect `JobTrack.slnx` and mutate the *entire solution* (confirmed: an
errant root-CWD run mutated 215 files across every project and took over 20 minutes, instead of the
~10-minute, single-project run this gate is scoped to be). The `StrykerOutput/` report always lands
under the CWD the command was run from, not under the config file's directory — a `StrykerOutput/`
appearing at the repository root is a sign the command was invoked from the wrong place.

A full run takes several minutes (build + coverage analysis + per-mutant test execution); do not
use a short timeout.

## Achieved score and break threshold

Last correctly-scoped run (`src/JobTrack.Domain/StrykerOutput/2026-07-12.09-51-01/reports/mutation-report.html`,
19.6s once build artifacts were warm; 211 mutants scored — 170 killed, 39 survived, 2 timeout, 0
`NoCoverage`; 91 excluded as `CompileError`, 262 excluded as `Ignored` (197 outside the `mutate`
scope, 65 removed by Stryker's "block already covered" filter)):

- **Achieved mutation score: 81.52 %** — Stryker's `(Killed + Timeout) / (Killed + Survived + Timeout + NoCoverage)`.
- **Configured `thresholds.break`: 75** — roughly 6 points of headroom below the achieved 81.52%,
  enough to absorb a future commit adding one or two more of the equivalent-mutant shapes
  documented below (every category found here recurs naturally as the codebase grows: boundary
  tie-breaks, guaranteed-non-empty `First()` calls, redundant null guards ahead of a
  self-null-checking LINQ call) without every such commit tripping the gate, while still catching
  a real regression — losing coverage on an entire genuine-gap category like the two closed below
  would drop the score well past this margin.
- **`thresholds.low` was raised from 60 to 75** in the 2026-07-09 run: `dotnet-stryker` 4.16.0 now
  validates `low >= break` at startup and refuses to mutate otherwise ("Threshold low must be more
  than or equal to threshold break"). This is a config-validation fix, not a scoring change — `low`
  only controls the report's colour banding, not the pass/fail gate, which remains `break: 75`.
- **A failing run on 2026-07-12 (74.88 %, below `break`) was the first automated run to catch a real
  regression, and it did its job.** Authorization policies added since the prior run —
  `PersonalAccessTokenPolicy` and `PersonalAccessTokenAccessPolicy` (ADR 0029) — entered the mutate
  scope automatically through the `Authorization/*.cs` glob but shipped with no `JobTrack.Domain`
  tests, contributing 16 `NoCoverage` mutants (counted against the score in the denominator above).
  Closed by two new test files under `tests/JobTrack.Domain.Tests/Authorization/` — see "Genuine
  gaps found and closed" §3–4 below — which took the score from 74.88 % to the 81.52 % recorded here
  and returned every mutant to `Killed`/`Survived` (0 `NoCoverage`).
- The mutant total rose from 170 (2026-07-09) to 211 as the `Authorization/*.cs`/`Costing/*.cs`
  globs picked up the requester-intake policies (ADR 0033: `RequesterAccessPolicy`,
  `JobPickupPolicy` (ADR 0031), `RequestHoldingAreaConfigurationPolicy`) and the two PAT policies
  added since. The two documented equivalent-mutant categories below grew accordingly — the
  null-guard category from 10 to 15 survivors, the exception-message-text category from 7 to 9 —
  with no change to any previously documented survivor's reasoning.

## CompileError mutants — not a concern

Every run reports a cluster of mutants with status `CompileError`. Diagnosed by isolating one
example in a throwaway console project: Stryker's "Linq method mutation (Count() to Sum())"
mutator does not distinguish `List<T>.Count` (a property) from the LINQ `Enumerable.Count()`
extension method — it pattern-matches on the token `Count` and rewrites some property-access sites
as if they were the extension method, replacing `eligiblePieces.Count == 0` with
`eligiblePieces.Sum() == 0`. `Sum()` has no applicable overload for a non-numeric element type
(e.g. `List<(CostableSession, WorkInterval)>`), so this is a genuine `CS1929` compiler error —
reproduced directly:

```
Program.cs(9,5): error CS1929: 'List<(int Session, int Interval)>' does not contain a definition
for 'Sum' and the best extension method overload 'Enumerable.Sum(IEnumerable<decimal>)' requires a
receiver of type 'System.Collections.Generic.IEnumerable<decimal>'
```

Stryker batches multiple non-conflicting mutants into a shared compilation per test run for
performance; when one mutant in a batch is a genuine compile error like the one above, the *whole
batch's build fails*, and every mutant scheduled in that batch — including otherwise-valid
neighbouring mutations (equality flips, block removals, logical operator swaps) at nearby source
locations in the same file — is reported as `CompileError` too, even though most of them would
compile fine in isolation. This was confirmed by manually re-checking several `CompileError`
mutants that had no `Count`/`Sum` involvement at all (e.g. `Rates/UserCostRate.cs`'s nullable
pattern-match mutations) but sat in the same file/batch as one that did.

This is not a defect in `JobTrack.Domain`: `dotnet build src/JobTrack.Domain -warnaserror` on
unmutated code is clean (0 warnings, 0 errors). It is also not the .NET 10
interceptors-on-by-default issue (Roslyn CS9137, stryker-net#3402) — `JobTrack.Domain` is a pure
library with no interceptor-consuming code. `CompileError` mutants do not count toward the
mutation score in either direction; they are excluded from both numerator and denominator.

Stryker's own "Safe Mode" catches a related shape proactively: `CostSegmentPartitioner.EligiblePieces`
uses `if (clippedToBounds is not { } clipped) { continue; }` — a mutator that flips this check can
produce a definite-assignment error on `clipped` (`CS0165`), which Stryker detects during mutant
creation and preemptively strips every mutation in that method rather than letting the batch-poisoning
above run its course. This shows up in the run log as a `WRN`, not as additional entries in the
final report's `CompileError` count.

## Documented equivalent / non-behavioural survivors

Stryker's own score already excludes `Ignored` mutants (the 53 outside the `mutate` scope) and
`CompileError` mutants. The remaining survivors below were triaged individually; each is either a
provably equivalent mutant (the mutated code produces byte-for-byte identical observable output to
the original for every reachable input) or a mutation of content this project deliberately does
not test (exception message text). None represent a real gap in test coverage. They are recorded
here — rather than chased with tests that couldn't actually distinguish the mutant from correct
code — so a future reviewer doesn't waste time re-deriving the same reasoning.

### Authorization null-guard removal (15 survivors across 11 policy files)

Each policy's `ArgumentNullException.ThrowIfNull(actorRoles)` guard, when deleted by a "Statement
mutation," is followed immediately by an `actorRoles.Contains(...)` call. `Enumerable.Contains` —
the LINQ extension method actually invoked here — throws `ArgumentNullException` (parameter name
`source`) on a null receiver internally. Removing the explicit guard therefore does not change the
observable exception *type* a caller sees; the existing `A_null_role_collection_is_rejected` tests
assert only the exception type (`Should().Throw<ArgumentNullException>()`), never the parameter
name, so they cannot and should not distinguish the two paths. Adding a `ParamName` assertion would
couple the test to which of two equally-correct null checks fired, which is not part of the
policies' contract. Ten files contribute one survivor per guarded method: `Achievement`, `Audit`,
`Cost`, `JobNode`, `Rate`, `Schedule` (one each), `WorkSession` (two — `CanManage`, `CanView`),
`EmployeeAccessPolicy` (three — `CanViewEmployee`, `CanManageRoles`, `CanManageAccounts`), and the
requester-intake additions `JobPickupPolicy`, `RequestHoldingAreaConfigurationPolicy` (one each) and
`RequesterAccessPolicy` (two — `CanSubmit`, `CanCommentAsRequester`). `RequesterAccessPolicy.CanView`
is *not* in this list — its guard mutant is killed, because that method's own null test passes
`actorIsRequestOwner: true`, so a removed guard would short-circuit `actorIsRequestOwner || …` to
`true` and wrongly succeed instead of throwing (the same short-circuit mechanism as
`PersonalAccessTokenAccessPolicy.CanManage` below). 15 survivors, 11 files.

**`PersonalAccessTokenAccessPolicy.CanManage` is the deliberate exception — its guard-removal mutant
is *killed*, not equivalent.** Unlike every policy above, `CanManage` short-circuits on
`actorId == targetUserId || actorRoles.Contains(...)`: when the actor is the target, a *removed*
guard would let `true || …` return before `Contains` is ever evaluated, so a null `actorRoles`
would wrongly succeed instead of throwing. `A_null_role_collection_is_rejected_before_the_self_short_circuit`
passes a self actor with null roles specifically to pin that the guard fires first, killing the
mutant that the non-self path could not distinguish.

### Exception message text (9 survivors: `AllocatedShare.cs` x2, `CostSegmentPartitioner.cs`,
`WorkInterval.cs`, `UserCostRate.cs`, `WeeklyInterval.cs` x2, `PersonalAccessTokenPolicy.cs` x2)

"String mutation" replaces an exception message argument with `""`. Message wording is not part of
this library's tested public contract (exception *type* and, where documented, `ParamName`/data
fields are); asserting exact message text would make tests brittle against copy-editing with no
corresponding gain in defect detection. The two `PersonalAccessTokenPolicy.cs` survivors are the
*message* arguments of its two `InvariantViolationException` throws; the new tests deliberately
assert `ConstraintId` (a stable, documented contract identifier) rather than the free-text message,
which is exactly why the message-string mutants survive while the constraint-selecting boundary
mutants on the same lines are killed.

### `IntervalAlgebra.cs` boundary tie-breaks (3 survivors: lines 21, 22, 53)

`Intersect`'s `a.Start > b.Start ? a.Start : b.Start` (and the symmetric `End` comparison) mutated
to `>=`/`<=` only changes which branch is taken when `a.Start == b.Start` (or the `End` equivalent)
— and on that tie, both branches evaluate to the same value, since `WorkInterval` is a
`readonly record struct` with value equality on its `Instant` fields. `Normalize`'s
`current.End > last.End` mutated to `>=` is the same shape: on a tie, the "no-op" branch and the
"replace with an equal value" branch are indistinguishable by any external observer.

### `WeeklyInterval.cs` `CrossesMidnight` (1 survivor: line 40)

`End <= Start` mutated to `End < Start`. The constructor already throws
`ArgumentOutOfRangeException` when `end == start` (line 21-23), so no validly-constructed
`WeeklyInterval` can ever reach the `End == Start` case this mutation would distinguish — `<=` and
`<` are equivalent over the type's entire reachable value space.

### `CostSegmentPartitioner.cs` boundary set construction (8 survivors: lines 51, 102, 126 x2,
126 logical, 130, 140 x2)

- Line 51 (`if (eligiblePieces.Count == 0) return [];` body removed) and line 102 (`SortedSet<Instant>`
  seeded with `{}` instead of `{ bounds.Start, bounds.End }`): when `eligiblePieces` is empty, the
  boundaries collapse to at most `{bounds.Start, bounds.End}` regardless, producing one candidate
  segment with zero active pieces — which `Partition`'s `if (active.Count == 0) continue;` already
  filters, so the returned allocation list is `[]` either way.
- Lines 126 (both the `&&`→`||` "Logical mutation" and the two "Equality mutation" halves) and 130:
  `AddClippedBoundaries` only accepts interior points strictly between `bounds.Start` and
  `bounds.End`. `SortedSet<T>.Add` is idempotent, and `bounds.Start`/`bounds.End` are already
  present in the set from the seeding above — so a mutation that additionally lets a boundary
  exactly *at* `bounds.Start`/`bounds.End` through adds nothing new. A mutation that lets exterior
  points (outside `[bounds.Start, bounds.End)`) through adds a boundary that can only ever produce
  a dead segment: every `eligiblePiece` is already clipped to `bounds` (via
  `IntervalAlgebra.Intersect(session.Interval, bounds)` in `EligiblePieces`), so no piece can be
  active on either side of an exterior boundary, and that segment is filtered by the same
  `active.Count == 0` check.
- Line 140 (`OrderBy(Start).ThenBy(End)` mutated to `OrderByDescending`/`ThenByDescending`):
  `ValidateNoSameLeafOverlap` only ever compares *adjacent* pairs in the sorted sequence, and
  `IntervalAlgebra.Overlaps` is symmetric. Reversing a sort order reverses the sequence but
  preserves exactly the same set of adjacent unordered pairs (`[a,b,c,d]` → adjacent pairs
  `(a,b),(b,c),(c,d)`; reversed `[d,c,b,a]` → adjacent pairs `(d,c),(c,b),(b,a)` — the same three
  unordered pairs). Whichever pair the loop happens to compare first differs, but whether *any*
  overlap is detected (the only externally observable outcome — the method throws or it doesn't)
  does not.

### `HierarchyDisplayReconciler.cs` (2 of 3 survivors: lines 31, 36)

- Line 31 (`if (residual == 0m) return [...]` body removed): when `residual == 0`,
  `Math.Sign(residual)` is `0`, so the general-path ranking key (`0 * (exact - rounded)`) is
  identically `0` for every child, the tie-break picks the lowest `JobNodeId` deterministically,
  and that child's rounded amount gets `+ 0m` applied — the same rounded value the fast path would
  have returned unmodified. Byte-for-byte identical result either way.
- Line 36 (`.First()` mutated to `.FirstOrDefault()`): `children.Count == 0` already returns early
  at line 21, so `naive` (built from `children`) is always non-empty when this LINQ chain runs —
  `First()` and `FirstOrDefault()` are equivalent on a guaranteed-non-empty sequence.

(The third `HierarchyDisplayReconciler.cs` survivor, the line-37 arithmetic mutation, was **not**
equivalent — see below.)

### `CostEngine.cs` (1 of 4 survivors: line 49)

`group.First().NodeId` mutated to `FirstOrDefault()`: `group` comes from
`allocations.GroupBy(allocation => allocation.SessionId)`, and `GroupBy` never yields an empty
group — `First()`/`FirstOrDefault()` are equivalent here for the same reason as above.

## Genuine gaps found and closed

Four categories of survivor were real test gaps, not equivalent mutants. The first two were found
and closed on the 2026-07-07 run; the last two (§3–4) are the PAT authorization policies whose
absence of any `JobTrack.Domain` test failed the gate at 74.88 % on 2026-07-12.

1. **`CostEngine.cs` trace/segment ordering (3 survivors: lines 54, 69, 70).** `OrderBy`/`ThenBy`
   on the segment trace's `Segment.Start`, `SessionId`, and each segment's `ActiveSessionIds`
   ordering had no test asserting the resulting order was ascending — only that the *set* of
   entries was correct. Closed by
   `tests/JobTrack.Domain.Tests/Costing/CostEngineTests.cs`'s
   `Concurrent_sessions_produce_a_deterministically_ordered_trace_and_active_session_ids`, which
   asserts `result.Trace` is in ascending segment-start order, that same-segment entries are
   ordered by ascending `SessionId`, and that `ActiveSessionIds` within a segment is in ascending
   session-id order.

2. **`HierarchyDisplayReconciler.cs` largest-remainder selection (1 survivor: line 37).** The
   ranking key `direction * (child.ExactAmount.Amount - child.Rounded.Amount)` mutated to `+`
   instead of `-` survived every existing test because those tests only used children whose
   *rounded* amounts were close in magnitude to each other, so adding `rounded` into the ranking
   key barely perturbed the ordering. Closed by
   `tests/JobTrack.Domain.Tests/Costing/HierarchyDisplayReconcilerTests.cs`'s
   `The_furthest_from_exact_child_is_selected_even_when_a_sibling_has_a_much_larger_rounded_amount`,
   which pairs a child with a small rounded amount but the larger rounding movement against a
   sibling with a much larger rounded amount but smaller rounding movement — the mutant selects the
   wrong child under this input, the correct implementation does not. Verified directly: the test
   passes against the real implementation and was confirmed to fail when `-` was manually changed
   to `+` in the source and reverted immediately after.

3. **`PersonalAccessTokenPolicy.cs` expiry validation (12 `NoCoverage` mutants: lines 28–36).**
   `EnsureValidExpiry` (ADR 0029) had no `JobTrack.Domain` test at all — its two boundary checks
   (`expiresAt <= now`, `expiresAt - now > MaxLifetime`) and the `MaxLifetime = 365 days` constant
   were entirely unexercised. Closed by
   `tests/JobTrack.Domain.Tests/Authorization/PersonalAccessTokenPolicyTests.cs`, which pins both
   boundaries exactly: an expiry *at* `now` and one *in the past* are rejected with
   `ConstraintId "personal-access-token-expiry-not-in-future"`; an expiry *exactly at* `MaxLifetime`
   is accepted while one a second beyond it is rejected with
   `ConstraintId "personal-access-token-expiry-too-long"`; and a normal future expiry is accepted.
   These kill every equality/negation mutant on both comparisons and both throw-removal mutants; the
   surviving message-string mutants are the documented equivalent category above.

4. **`PersonalAccessTokenAccessPolicy.cs` issue/manage authorization (4 `NoCoverage` mutants: lines
   15, 24, 26).** `CanIssue` (self-only) and `CanManage` (self-or-`Administrator`, ADR 0029) had no
   `JobTrack.Domain` test — a `==`→`!=` mutant on either identity check, or a `||`→`&&` on
   `CanManage`, would have silently inverted who may mint or revoke a credential. Closed by
   `tests/JobTrack.Domain.Tests/Authorization/PersonalAccessTokenAccessPolicyTests.cs`: `CanIssue`
   holds for self and not for another user; `CanManage` holds for self *without* the administrator
   role (killing the `||`→`&&` and `==`→`!=` mutants on the identity branch) and for an administrator
   over another user, and is false for a non-administrator over another user. Its null-guard mutant is
   killed rather than left equivalent — see the short-circuit note under "Authorization null-guard
   removal" above.
