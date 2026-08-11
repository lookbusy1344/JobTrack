# ADR 0019: House style — functional core with .NET exceptions as the sole failure channel

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 18

## Decision

The domain and application layers (`JobTrack.Domain`, `JobTrack.Application`) are written functional-first:

- **Immutable data by default:** `record`/`readonly record struct` for value objects, `init`-only or `required` members, no mutable public state.
- **Pure functions for domain logic** — the cost engine (§7.2) is the exemplar: deterministic, side-effect-free over immutable, fully materialized input plus one `asOf`.
- **No mutable persistence entity graphs escape into the domain** — EF-tracked entities stay inside the persistence assemblies (§7.4); the domain and application layers only ever see immutable contracts.
- **Exhaustive `switch` expressions over closed enums** (`Achievement`, `RateSource`, schedule-exception effect) — no silent `default` fallthrough, so adding a case to a closed enum is a compile-time obligation everywhere it is matched.
- Modern idiomatic C# where it improves clarity: primary constructors, file-scoped namespaces, pattern matching, collection expressions, target-typed `new`, `nameof`.

**The one deliberate departure from functional purity: error handling follows .NET exception idioms 100%, everywhere.** Never a `Result`/`Either`-style error channel, internally or at any boundary. Every failure throws:

- framework exceptions for caller/usage errors (`ArgumentException`, `ArgumentNullException`, `ArgumentOutOfRangeException`, `InvalidOperationException`, `OperationCanceledException`);
- the shallow `JobTrackException` hierarchy (§7.1, §12.6) for conditions callers handle distinctly: not found, authorization denied, concurrency conflict, prerequisite blocked, missing rate, and invariant violation (see ADR 0018 (`0018-invalid-overlap-cost-engine.md`) for one concrete instance).

**`Try*` is a relief valve, not a default shape.** Throwing is the default for every member. A `Try*` member (returning `bool` plus an `out` result) is introduced only as FDG's performance accommodation — a measured hot path or a common-failure/expected-absence scenario, exactly `int.TryParse`'s rationale — and it **complements** a throwing member rather than replacing it. It signals success/failure and a value only, never a failure *category*; a caller needing to distinguish *why* something failed uses the throwing member and catches the typed exception. Introducing a new `Try*` member requires the same evidence-driven justification as any other performance optimisation — it is not added reflexively to every query "just in case."

This is not treated as a contradiction of functional style but as the single sanctioned exception to it (plan §5.1 item 18's own framing) — a pure function that throws on an invalid input is still referentially transparent for every input on which it does not throw, and the alternative (threading a `Result<T, TError>` through every call site) was rejected as inconsistent with idiomatic .NET and with the "exceptions are the sole failure channel" project-wide convention.

## Consequences

- The library gate (§7.5) and the review prompts in §14.2 explicitly check: does every failure throw a .NET exception with no error-code returns and no `Result`/`Either` channel anywhere; is a `Try*` member's existence justified by measured need rather than reflex.
- Analyzer configuration (`Directory.Build.props`) should flag any hand-rolled result/either type introduced into `JobTrack.Domain` or `JobTrack.Application` as a style violation requiring explicit sign-off, since it directly contradicts this ADR.
- `JobTrackException` and its subtypes are documented (XML doc, plan §4) on every public member that can throw them, since callers rely on the documented exception contract to decide what to catch — an undocumented `JobTrackException` subtype thrown from a public API is treated as an API design defect, not an oversight to patch later.
