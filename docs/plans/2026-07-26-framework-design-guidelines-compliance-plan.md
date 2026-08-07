# Framework Design Guidelines compliance audit and remediation

**Date:** 2026-07-26
**Status:** Implemented. Six of the eight findings are remediated (§2.1, §2.2, §2.3, §2.4, §2.7,
§2.8). Two were withdrawn on closer inspection rather than implemented — §2.5 and §2.6 — each
recorded below with the evidence that retired it. Gates run per step: `dotnet build -warnaserror`,
`dotnet format`, `fast-test.sh --build`, plus targeted provider suites for the persistence changes
and `JobTrack.Web.IntegrationTests --filter Cost` for the money-formatting change.
**Scope:** The reusable library assemblies only — `JobTrack.Abstractions`, `JobTrack.Domain`,
`JobTrack.Application`, `JobTrack.Persistence.Shared`, `JobTrack.Persistence.PostgreSql`,
`JobTrack.Persistence.Sqlite`. `JobTrack.Web`, `JobTrack.AdminCli`, and `JobTrack.Database` are
application hosts and are exempt from public-API discipline per `CLAUDE.md`.
**Measured against:** `../Framework_Design_Guidelines_Essentials.md` (revision 2026-07-26).
**Depends on:** the public API discipline in `CLAUDE.md` ("Conventions" → Public API discipline) and
the library gate in `docs/plans/jobtrack_impl_plan.md` §7.5.

## 1. Summary

The library surface is in good shape against the guidelines. The exception hierarchy (ch. 7), enum
design (ch. 4), TAP conformance (ch. 9), strongly typed identifiers (ch. 4 "strongly typed strings"
applied to ids), and exception documentation are all exemplary and need no work. `AuditEventId` is a
model implementation of the complete `IComparable<T>` + comparison-operator set, and
`EquatableArray<T>`/`EquatableDictionary<K,V>` both correctly guard their all-zero state.

Deliberately **not** treated as findings, per the instruction accompanying this audit: the public
surface being expressed as interfaces (`IJobTrackClient` and its twelve sub-services) rather than
classes. Ch. 4's "favour classes over interfaces" does not apply here.

Eight findings follow, ranked. Item 1 is the only one with real shipping consequences; the rest are
consistency and polish against a documented standard.

## 2. Findings

### 2.1 `JobTrack.Persistence.Shared` ships an unintended public surface — High

Its own csproj `<Description>` asserts: *"Every type is internal; nothing here is part of the public
library surface."* That is false. The project exposes:

| File | Public surface |
|---|---|
| `JobNodeWriteExceptionTranslation.cs` | `public static class` + 4 public methods |
| `JobNodeHierarchyQueries.cs` | `public static class` + 8 public methods |
| `JobTrackModelConfiguration.cs` | `public static class` + `Configure` |
| `AuditEventWriter.cs` | `public static class` + `Add` |
| `JobNodeHierarchyQueries.cs` | 4 public records: `AncestorChainRow`, `SubtreeAchievementRow`, `RequesterSubtreeRow`, `BoundedSubtreeRow` |

Three things compound this:

- the project **is packaged** (`PackageId`, `PackageReadmeFile`, `None Include="README.md" Pack=true`),
  so this surface ships to consumers;
- it is the only library project **without** `EnablePublicApiAnalyzers` and `PublicAPI.*.txt`, so
  none of it is tracked or reviewable as an API change (Modern Supplement, "Make compatibility checks
  executable");
- it already declares `InternalsVisibleTo` for both providers and its own test project — which is
  exactly the mechanism that makes `public` unnecessary.

Ch. 1: *"Every public element is a lifetime commitment."* Appendix D: removing a public type later is
in the "usually unacceptable" column. Right now the commitment is being made accidentally.

**Remediation.** Change all eight types to `internal`. `InternalsVisibleTo` already covers every
legitimate consumer, so this should compile unchanged; if anything outside those three assemblies
breaks, that is itself the finding and needs a deliberate decision rather than a silent `public`.
Then either add `EnablePublicApiAnalyzers` with an empty baseline to keep it honest, or drop the
packaging properties if the assembly is not meant to ship standalone.

### 2.2 No assembly-level compliance attributes — Medium

Ch. 4, Assemblies: *"**DO** apply `CLSCompliant(true)`, `AssemblyVersion`, and informational
attributes to any assembly with public types. **CONSIDER** `ComVisible(false)`, `AssemblyFileVersion`,
and `AssemblyCopyright`."*

`Directory.Build.props` sets `Version`, `Authors`, `RepositoryUrl`, SourceLink, and symbols — good —
but `grep` finds no `CLSCompliant`, `ComVisible`, or `AssemblyCopyright` anywhere in the repository.
`CLSCompliant(true)` is the one that carries weight: it is the compiler-enforced check that the
public surface is consumable from other CLR languages, which ch. 2 lists as a first-class design
obligation (*"DO design for the broad variety of CLR languages"*). Without it, nothing verifies that
claim.

**Remediation.** Add to `Directory.Build.props`, scoped to the library projects (hosts don't need it):

```xml
<PropertyGroup>
  <Copyright>...</Copyright>
</PropertyGroup>
```

plus an `AssemblyInfo.cs` (or `AssemblyAttribute` items) carrying `[assembly: CLSCompliant(true)]`
and `[assembly: ComVisible(false)]`. Expect the CLS check to flag things — resolve each on its
merits rather than suppressing `CA1014`/`CS3021` wholesale. Note `ulong`/`uint`-typed public members
and unsigned EF column mappings are the usual offenders; the codebase's use of `long`/`short`
throughout suggests this will be close to clean.

### 2.3 `Money` and `HourlyRate` are incomplete as primitive-like value types — Medium

Both are `readonly record struct` wrappers over an ordered `decimal`. Neither implements
`IComparable<T>`, defines comparison operators, or overrides `ToString`.

Three separate rules bear on this:

- **Ch. 5, operators:** *"**AVOID** operator overloads except on types that should feel like
  primitives (e.g. `Decimal`)."* `Money` is precisely that category — the exception, not the
  prohibition.
- **Ch. 8, `ToString`:** *"**DO** override it when a useful human-readable string exists… **DO**
  offer `IFormattable` or `ToString(format)` for culture-sensitive output."* Today
  `$"{someMoney}"` renders `Money { Amount = 12.500000 }` — the compiler-generated record
  `ToString`. That is a plausible way to leak an unformatted amount into a log or a page.
- **Ch. 8, equality:** *"**CONSIDER** overloading `<`, `>`, `<=`, and `>=` when implementing
  `IComparable<T>`."*

`AuditEventId` already implements exactly this pattern correctly and is the template to copy.

**Remediation.** On both types: implement `IComparable<T>`, add the complete `<`/`>`/`<=`/`>=` set
(ch. 5: *"DO provide the complete natural set"*), and override `ToString()` to render the amount.
For `Money`, `ToString("C", provider)` via `IFormattable` is the right shape given the
installation-wide GBP assumption; keep the culture explicit rather than relying on ambient
`CurrentCulture`, consistent with how the project already treats time zones. Adding arithmetic
operators (`+`, `-`, `*` by a scalar) is a separate judgement call — the cost engine currently does
its arithmetic on raw `decimal`, so this may be deliberate; decide explicitly rather than by default.

### 2.4 `AllocatedShare`'s all-zero state is invalid — Medium

Ch. 4, §4.2: *"**DO** ensure the all-zero state is valid."*

`AllocatedShare`'s constructor rejects a non-positive `SegmentTicks` or `ConcurrencyDivisor`, but
`default(AllocatedShare)` bypasses it and yields `ConcurrencyDivisor == 0`. Since the type's whole
contract is *"the share is `SegmentTicks / ConcurrencyDivisor`, computed exactly wherever it is later
consumed"*, a defaulted instance is a division by zero waiting at an unspecified downstream call
site — including inside a `default`-initialised array or a `default!`-assigned field.

The validating constructor is right; the gap is that it is not the only way in.

**Remediation.** Two defensible options — pick one deliberately:

- **Make zero meaningful.** Treat `ConcurrencyDivisor == 0` as "no share", and have consumers
  handle it explicitly. Cheapest, but weakens the type's invariant.
- **Guard the read.** Expose the quotient through a member that throws
  `InvalidOperationException` on the defaulted state (ch. 7's "object in wrong state" case), so the
  failure surfaces at the type rather than as an arithmetic exception three layers away.

The second matches the project's existing `EquatableArray<T>` approach (which normalises its default
on read) and is the recommendation. Either way, add a test asserting the chosen behaviour of
`default(AllocatedShare)`.

### 2.5 `TryDecode` has no exception-throwing counterpart — WITHDRAWN

Ch. 7, Try pattern: *"**DO** provide an exception-throwing counterpart for each Try member."*

`AuditEventCursorCodec` exposes `Encode(cursor) -> string` and `TryDecode(string, out cursor) -> bool`
but no `Decode(string) -> AuditEventSearchCursor`. The pairing the guideline expects is
`Encode`/`Decode` with `TryDecode` as the relief valve; the asymmetry is visible in the type's own
member list. It also matches the ch. 9 factory guidance naming conversions `Parse`/`Decode`.

**Withdrawn — not implemented.** The finding does not survive inspection of the type's accessibility
and its caller. `AuditEventCursorCodec` is `internal`, and the Try-pattern rule governs *public*
surface; ch. 5 separately says not to add members for hypothetical future need. Its one caller,
`AuditQueries.SearchAuditEventsCoreAsync`, already converts a malformed cursor into
`ArgumentException("The audit search cursor is malformed.", nameof(request))` — so the throwing
counterpart exists, correctly placed at the public boundary and naming the parameter a consumer
actually passed. A `Decode` on the internal codec would be dead API duplicating that.

### 2.6 `await using` declarations omit `ConfigureAwait(false)` — WITHDRAWN

Ch. 9, TAP: *"**DO** use `ConfigureAwait(false)` except where the app model depends on the
synchronization context."*

Explicit `await` expressions are **fully compliant** — 51/51 in `JobTrack.Application`, 20/20 in
`JobTrack.Persistence.Shared`, and every one in both providers carries `.ConfigureAwait(false)`.
That is genuinely good discipline.

The gap is the implicit await in `await using` declarations: 164 sites across
`JobTrack.Application` and the two providers, e.g. `await using var context = CreateContext();`.
The generated `DisposeAsync()` await captures the context. Practical impact under ASP.NET Core is
nil (no `SynchronizationContext`), but these are reusable library assemblies whose consumers'
app model is not knowable, which is the situation the rule exists for. Appendix A even contemplates
this case explicitly, permitting braceless `await using` *"to simulate stacked `await using`
statements with `ConfigureAwait` in a fresh scope."*

**Withdrawn — not implemented.** This plan's original remediation ("mechanical, but it touches 164
lines") was wrong about the cost, and the corrected cost changes the answer.

`ConfigureAwait` on an `IAsyncDisposable` returns `ConfiguredAsyncDisposable`, which exposes only
`DisposeAsync`. So the one-line append does not compile — the variable becomes unusable:

```
error CS1061: 'ConfiguredAsyncDisposable' does not contain a definition for 'Value'
```

Every site must instead split into two statements plus a throwaway name:

```csharp
var context = CreateContext();
await using var contextDisposal = context.ConfigureAwait(false);
```

Across 164 sites that is ~164 extra lines and 164 invented names through the query ports, against a
benefit that is **zero** in the actual deployment — ASP.NET Core installs no `SynchronizationContext`,
so the captured context is the null context either way — and hypothetical only for a WinForms/WPF
consumer of a PostgreSQL or SQLite persistence library.

Weighed against ch. 1's "optimise for the consumer" and the readability cost, the guideline is not
worth following here. Recorded as a deliberate, evidenced deviation rather than an oversight. Note
the surrounding discipline is genuinely good and unaffected: **every explicit `await` in the six
library projects already carries `.ConfigureAwait(false)`** — 51/51 in `JobTrack.Application`, 20/20
in `JobTrack.Persistence.Shared`, and all of both providers'.

### 2.7 Mojibake in `IJobTrackClient`'s XML documentation — Low

Ch. 2: *"**DO** still ship great documentation."* Appendix A: *"**DO** keep source ASCII and use
`\uXXXX` escapes for non-ASCII."*

`src/JobTrack.Application/IJobTrackClient.cs` has 11 instances where a `§` has been corrupted to `?`:
`plan ?7.1`, `spec ?13.2`, `plan ?7.3 step 2`, and so on. This is the entry-point type of the whole
library — the first documentation a consumer reads in IntelliSense — and the corruption makes every
spec cross-reference in it unreadable. It is the only file in `src/` affected.

**Remediation.** Repair the 11 references. The house convention elsewhere in the codebase is to
write `§` literally (it appears correctly in dozens of other files), so match that rather than
introducing escapes; the Appendix A ASCII rule is a BCL house style the project has already
consciously departed from.

### 2.8 `static readonly T[]` constant tables — Low

Ch. 9, §9.12 (*"Read-only arrays and collections"*, added to the guidelines 2026-07-26): *"**DO NOT**
declare a constant table as `static readonly T[]`. The `readonly` modifier gives no protection to the
elements. **DO** prefer a `static ReadOnlySpan<T> X => [...];` property."*

Four sites:

| File | Field |
|---|---|
| `JobTrack.Persistence.PostgreSql/PostgreSqlEmployeeQueryPort.cs:18` | `private static readonly short[] WorkflowRoleIds` |
| `JobTrack.Persistence.Sqlite/SqliteEmployeeQueryPort.cs:17` | `private static readonly short[] WorkflowRoleIds` |
| `JobTrack.Persistence.PostgreSql/PostgreSqlAuditQueryPort.cs:90` | `private static readonly string[] SensitiveEntityTypes` |
| `JobTrack.Persistence.Sqlite/SqliteAuditQueryPort.cs:80` | `private static readonly string[] SensitiveEntityTypes` |

All four are `private`, so ch. 5's *"DO NOT assign a mutable instance to a public or protected
`readonly` field"* is not violated and no consumer can reach them. The exposure is intra-class only:
each reads as an immutable table but any method on the type could write `WorkflowRoleIds[0] = 0`.

Severity is genuinely low, and there is a real constraint: `SensitiveEntityTypes` is used in EF LINQ
`Contains` translation, where a `ReadOnlySpan<string>` will not work — expression trees cannot
capture a `ref struct`. `WorkflowRoleIds` likely has the same problem.

**Remediation.** Check each site's usage before converting.

- Where the value feeds an EF expression tree, `ReadOnlySpan<T>` is not available; use
  `static FrozenSet<T>` (better `Contains` performance than an array anyway) or
  `static IReadOnlyList<T>`, per §9.12's fallback clause.
- Where it is used only in ordinary C#, convert to the `static ReadOnlySpan<T> X => [...]` property
  form.

Do not force the span form where it does not fit — §9.12 explicitly permits the fallback, and this
is exactly the case it names.

## 3. Observations — no action proposed

Recorded so a later reviewer does not re-derive them. Each is a defensible deviation, not a defect.

- **`IReadOnlyCollection<EmployeeRole>` on authorization policy inputs** (nine sites in
  `JobTrack.Domain/Authorization`). Ch. 5 says accept the least-derived type that works
  (`IEnumerable<T>`), and ch. 8 says *"AVOID `ICollection<T>` just to read `Count`"*. But these
  policies enumerate the roles repeatedly within one call, and `IReadOnlyCollection<T>` documents
  that the argument must be a materialised set rather than a deferred query — a defensible
  pit-of-success choice over a literal reading of the rule.
- **`EquatableDictionary<K,V>.GetEnumerator()` returns `Dictionary<K,V>.Enumerator`.** Technically
  surfaces an implementation type in a public signature (ch. 8), but this is the standard
  allocation-free `foreach` idiom the BCL itself uses. Keep it.
- **`AuthenticationAuditEventKind` has no explicit member values** and its zero is a meaningful
  member (`LoginSuccess`), so `default` is a real event rather than an unset marker. Every other
  enum in the codebase assigns explicit values and reserves zero (`None`/`Unspecified`/`Unfinished`).
  Worth aligning if the enum is ever persisted by ordinal; harmless otherwise.
- **Three public members take two `bool` parameters** (`WorkSessionAccessPolicy.CanFinishSession`,
  `RequesterAccessPolicy.CanSubmit`, `JobNodeStructuralProjection.ToResult`). Ch. 5: *"DO prefer an
  enum over two or more Boolean parameters."* All three have self-documenting parameter names and
  are called from a handful of sites; an enum would be heavier than the problem.
  **Superseded (2026-08-07):** a later fresh-eyes review found this observation covered only three
  of several public Boolean-cluster call shapes and did not weigh a nominal fact-record contract
  against an enum for the wider set (up to five parameters, including `RequesterAccessPolicy.CanView`/
  `CanCommentAsRequester` and `LeafReopenAndStartAccessPolicy.CanReopenAndStartFor`, not named here).
  `docs/plans/2026-08-07-framework-design-guidelines-follow-up-remediation-plan.md` §3.1 replaced the
  Boolean-cluster methods this observation covers (`CanSubmit`, `CanView`, `CanCommentAsRequester`,
  `CanReopenAndStartFor`) with fact-record parameters; `JobNodeStructuralProjection.ToResult` is
  `internal` and stayed out of that plan's scope, so it is undecided, not accepted, going forward.
- **Every `PublicAPI.Shipped.txt` is empty** (1 header line) with the full surface in
  `Unshipped.txt` — 290 + 2,376 + 439 + 3 + 3 entries. Correct for pre-release. Promoting to
  `Shipped.txt` is the natural mechanical marker for passing the library gate (§7.5), at which point
  `CLAUDE.md`'s compatibility commitment starts to bind.
  **Superseded (2026-08-07):** the library gate (ADR 0026, M6) and the 1.0 release gate (ADR 0063)
  have since passed, but the promotion this observation anticipated did not happen at either
  acceptance — every `PublicAPI.Shipped.txt` still held only the nullable header.
  `docs/plans/2026-08-07-framework-design-guidelines-follow-up-remediation-plan.md` Stage 1 performed
  the mechanical Unshipped→Shipped promotion this observation describes; see ADR 0013's dated
  implementation note.

## 4. Sequencing

Ordered so each step is independently committable and testable, per the commit gate in `CLAUDE.md`.

| # | Work | Outcome |
|---|---|---|
| 1 | §2.1 — `internal`-ise `JobTrack.Persistence.Shared` | **Done.** All eight types are `internal`; compiled unchanged, confirming `InternalsVisibleTo` already covered every consumer. Empty `PublicAPI` files plus the analyzer now make the csproj's "every type is internal" claim an RS0016 build error — verified by temporarily re-publicising `AuditEventWriter` and observing the failure. Packaging retained: both providers reference it, so it must remain packable. |
| 2 | §2.7 — repair the corrupted `§` references | **Done.** Eleven section signs plus one em dash. A repo-wide scan for other mojibake markers found none. |
| 3 | §2.4 — `AllocatedShare` defaulted state | **Done.** Added `IsUninitialized`, validated at `SegmentCostCalculator.Calculate` with `ArgumentException` rather than throwing from a getter (ch. 5 advises against the latter). Two new tests on the type, one on the calculator. |
| 4 | §2.3 — `Money`/`HourlyRate` value-type contract | **Done.** `IComparable<T>`, the full comparison-operator set, and `IFormattable`. Formatting moved onto the types and `MoneyDisplay` now delegates; rendered output is byte-identical. Arithmetic operators deliberately not added. |
| 5 | §2.5 — `Decode` counterpart | **Withdrawn** — see §2.5. |
| 6 | §2.8 — the four constant tables | **Done.** `WorkflowRoleIds` → `IReadOnlyList<short>` (EF expression trees cannot capture a `ref struct`); `SensitiveEntityTypes` → `FrozenSet<string>` (in-memory membership test). Both providers' employee and audit suites confirm the EF `Contains` still translates. |
| 7 | §2.2 — `CLSCompliant(true)` / `ComVisible(false)` | **Done.** Abstractions, Domain, Application, and Persistence.Shared pass unchanged. The providers' `Create` factories cannot — their parameters come from dependencies that do not declare compliance (`IPasswordHasher<T>`, `NpgsqlDataSource`) — so those two members carry `[CLSCompliant(false)]` rather than the assemblies dropping the claim. Added `Copyright`. |
| 8 | §2.6 — `ConfigureAwait(false)` on `await using` | **Withdrawn** — see §2.6. |

Two findings did not survive implementation. That is the audit working as intended: §2.5 and §2.6
were both measured correctly against the guidelines but failed on facts that only surfaced when the
change was attempted — an `internal` type whose throwing path already sits at the public boundary,
and a transformation an order of magnitude more invasive than the source rule implies.

A caveat on the money-formatting change (§2.3): the deployed image runs in ICU-less
globalization-invariant mode, so the standard `"C"` specifier and any `en-GB` culture lookup both
throw. `SterlingFormat` keeps the literal-symbol/`InvariantCulture` approach the web host had
already adopted for that reason. Anyone revisiting money rendering must preserve that constraint.

## 5. Guidelines document change

This audit was measured against a revision of `../Framework_Design_Guidelines_Essentials.md` that
did not yet cover book §9.12. That section — read-only arrays and collections, and the
`static ReadOnlySpan<T> X => [...]` property form — was missing from the digest entirely; the
document carried only the adjacent ch. 5 public-field rule and the ch. 8 "arrays are mutable"
remark, neither of which reaches the `static readonly T[]` case or names a remedy.

The section has been added to the document's chapter 9 with a cross-reference from ch. 5, and the
document now carries a revision date. Finding §2.8 is measured against it.
