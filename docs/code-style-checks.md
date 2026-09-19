# CodeStyle architecture guards

Syntax-level house-style rules run as xUnit tests in
`tests/JobTrack.ArchitectureTests`, one class per rule, named `CodeStyle_<ShortName>`. Each scans
the tracked `.cs`/`.cshtml` sources under `src`, `tests`, and `samples` (or a narrower scope, noted
below) and fails with file:line violations. They run as part of the normal test suite — no separate
invocation.

Adding a new syntax-level rule needs its own `CodeStyle_<ShortName>` guard here; see CLAUDE.md
house style for the requirement.

## CodeStyle_EmptyBracesPattern

Bans the empty property pattern with binding: `x is { } y` / `x is not { } y`, in any expression or
statement position. It hides a null test and a rebind behind empty braces. A named type pattern
(`x is SomeType y`) is real pattern matching and stays allowed, as does a non-empty property pattern
(`x is { Length: 0 } y`) and a positional pattern.

No exception mechanism.

## CodeStyle_NestedConditional

Bans a ternary nested inside another ternary — in either branch or the condition, at any depth,
including the chained `a ? x : b ? y : z` form. Two ternaries as siblings (separate arguments,
separate statements) are fine. Rewrite as a `switch` expression, or extract the inner conditional
into a named local.

No exception mechanism.

## CodeStyle_PostfixIncrement

Requires the prefix form (`++count`) wherever the increment/decrement result is discarded: a bare
`count++;` statement, or a `for` loop's `i++` incrementor. The postfix form stays wherever the
pre-increment value is actually read (`var b = a++;`, `buffer[i++] = x;`, a loop condition).

No exception mechanism.

## CodeStyle_SystemThreadingLock

Requires every `lock` statement's target to have compile-time type exactly `System.Threading.Lock`,
not `object` or another reference type via `Monitor`-based locking. Preserves the compiler's
optimized lock lowering for `Lock`. Uses a semantic model (real type resolution), not a syntax match
on the declared type's name.

No exception mechanism.

## CodeStyle_MethodLength

Caps a method at **75 executable lines** — blank, comment-only, and brace-only lines excluded; a
local function or accessor nested inside is measured on its own, not folded into the enclosing
method's count. Applies to methods, local functions, and accessors alike, in both `.cs` and
Razor `@functions`/`@code` blocks. Razor's own generated rendering method (the markup-to-`WriteLiteral`
lowering) is not measured — only authored code blocks are.

Relief valve: `[LongMethod("reason")]` (`JobTrack.Abstractions.CodeStyle`) on the declaration, with
a reviewed justification string. Never a routine escape — decompose first; only use with explicit
user permission. Wrapping code in an after-`return` local function that closes over the caller's
locals just to shrink the outer count is explicitly forbidden by house style, since the guard
measures local functions separately and this would just hide lines without improving the code.

## CodeStyle_FileLength

Hard ceiling on file size, split by directory and file kind — **no exception mechanism**, an
overlong file is divided along a cohesive type, capability, or scenario boundary (partial classes
for the mechanical case, partial views for Razor):

| Location                        | Kind         | Measured as | Ceiling |
|----------------------------------|--------------|-------------|---------|
| `src/`, `samples/`               | `.cs`        | code lines  | 1,000   |
| `src/`, `samples/`               | `.cshtml`    | physical lines | 500  |
| `tests/`                         | `.cs`        | code lines  | 2,000   |

"Code lines" count a line carrying at least one token — blank lines and comment-only lines are free,
so documentation costs nothing. Razor is measured in physical lines since its markup and comments
interleave densely. A multi-line string literal counts every physical line it spans.

## CodeStyle_StructSize

Framework Design Guidelines size ceiling for value types: a struct or record struct over
**24 bytes** of instance layout should become a class, since every pass-by-value copies the whole
thing. Measured via reflection over the compiled assemblies — real runtime layout (`decimal` is 16
bytes, an `Instant` is 16, a reference field is always 8) rather than a syntax-level guess — covering
every non-generic struct plus every closed generic struct instantiation found in the assemblies'
ECMA-335 metadata. Enums are not value types for this purpose. Compiler-generated structs
(`[GeneratedCode]`/`[CompilerGenerated]` — async state machines, `[LoggerMessage]` parameter
carriers) are excluded; an open generic struct definition can't be measured as a concrete layout and
is skipped in favour of its closed instantiations.

Relief valve: `[LargeStructAttribute]` (`JobTrack.Abstractions.CodeStyle`) on a struct authored
deliberately above the ceiling (e.g. `WorkInterval`, a zero-allocation `foreach` enumerator), with
its own reviewed justification — the same "earns its place only by review" bar as the mutable
constant-table allowlist.

## CodeStyle_ConstraintIdIsNamed

Every `InvariantViolationException` constructor call that carries a constraint id (2+ arguments,
where a 2-argument call is disambiguated from the `(message, innerException)` overload by the second
argument being string-shaped) must pass a named `ConstraintIds` member, never a string literal typed
independently at the throw site. A throw site and a catch site that switches on the same id
previously carried two independently typed literals, where a typo in either compiles silently.
`ConstraintIds.cs` itself is exempt, since its members are where the literal values live.

Scope: `src/` only (not `tests/`/`samples/`). No exception mechanism.

## CodeStyle_CommitInsideTranslation

Narrower than the others — scoped to `WorkSessionCommandPort.cs` specifically, not the whole
repository. Requires that any `transaction.CommitAsync()` wrapped in a `try` that translates write
conflicts also catches `DbUpdateConcurrencyException`. A commit's `try` that translates one failure
class but not the EF optimistic-concurrency exception lets that exception leak out of the library
raw. A bare `CommitAsync()` outside any `try` is untouched.

Structural provider ports (Move, schedule/rate inserts) also wrap a commit but can't raise
`DbUpdateConcurrencyException` (server-side SQL check, or pure inserts), so a repository-wide version
of this rule would mis-fire on them; their commit auditing is handled by the classifier and port
work directly instead of a blanket guard.

No exception mechanism.
