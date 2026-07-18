# ADR 0043: Browse subtree rows carry their own readiness, and only blocked rows are marked

**Status:** Accepted
**Extends:** ADR 0039 (Browse's bounded multi-level subtree).

## Context

Browse shows readiness for exactly one node — the one being viewed — as a pill in its record card.
`JobQueries.GetJobSubtreeAsync` returns no readiness at all, so the subtree table beneath it says
nothing about which of its rows can actually be worked. Someone scanning a tree for something to
pick up has to open each leaf in turn to find out.

Readiness is not a property a row can be given cheaply. Spec §6 (`ReadinessCalculator`) defines a
node as ready only when every prerequisite attached **to it or to any of its ancestors** is
satisfied, where satisfaction means the required job's derived achievement is `Success` — and
achievement derivation is itself recursive over the required job's whole subtree. A prerequisite
may point at a job anywhere in the hierarchy, inside the fetched subtree or far outside it.

`IReadinessQueryPort.GetReadinessInputsAsync` materializes exactly those facts. Its documented
contract describes them as scoped to one node's ancestor chain, which would have made per-row
readiness an N+1 over a subtree bounded only by `JobSubtreeLimits.BreadthCap ^ HardMaxDepth`, and
forced a new set-based port method into both providers. **Both implementations in fact load the
entire `job_node` table and every `job_prerequisite` edge**, ignoring the `nodeId` parameter except
to throw `EntityNotFoundException`. The parameter narrows the contract, not the query.

So the inputs for one node are already the inputs for every node, and no port, provider, or schema
change is needed — only the Application layer's use of what it already fetches. The doc comment on
`ReadinessQueryResult` is corrected to describe what is actually materialized.

Two questions had to be settled before any of that could be built.

## Decision

### 1. A row's readiness is its own, aggregated over its ancestors — never over its descendants

A branch row reports the same thing `ReadinessCalculator` would report if that branch were the node
being viewed: blocked when a prerequisite on it or on one of its ancestors is unsatisfied. A branch
is **not** marked blocked merely because some descendant leaf of it is blocked.

Rejected alternative: aggregate upward, so a branch is blocked when any descendant is. It reads
plausibly ("something in here is stuck") but it makes the same word mean two different things on
one screen — the record card above the table would say Ready while the same node's row said Blocked
— and it is not what spec §6 defines readiness to be. One definition, evaluated per row.

A consequence worth stating: because prerequisites inherit downward, a blocked node blocks its
entire subtree. Marking the whole subtree is correct, not a bug — an ancestor's unsatisfied
prerequisite really does gate every descendant.

### 2. Only blocked rows are marked

A blocked row carries the red stop palm. A ready row carries nothing.

Readiness is not a balanced pair of states worth equal ink here: in a healthy tree nearly every row
is ready, and a green sign on every row would be noise that buries the few rows that matter, in the
one view whose job is scanning. Absence of the sign means ready. The record card keeps both states
(`Ready` / `Blocked`), because there a single pill answers a question the reader explicitly asked.

This is the only place the stop/go pair is deliberately used one-sided; `docs/design-language.md`
records the rule.

### 3. Readiness inputs are materialized once and evaluated per row in the Application layer

`GetJobSubtreeAsync` calls the existing `IReadinessQueryPort.GetReadinessInputsAsync` once and runs
the pure `ReadinessCalculator` per row against that one input set. No port method is added, no
provider changes, no schema change. The calculator stays untouched; no graph traversal moves into
persistence, and none is written in the web layer.

Deliberately **not** done: adding a set-based port overload to make the narrowed contract real. That
is worth doing — see Consequences — but it is a scale fix to an existing query, not part of showing
readiness on a row, and bundling it here would put a persistence-phase change inside a Browse
feature.

## Consequences

- The subtree query costs one additional round trip, independent of row count.
- `JobSubtreeNodeResult` and the external HTTP API's subtree DTO both gain an `IsReady` field. It is
  additive, so no existing client breaks.
- Readiness is now reported in two places from one calculator. If spec §6's definition ever changes,
  both move together — there is no second implementation to keep in sync.
- **Known scale debt, pre-existing and now more load-bearing.** Every readiness evaluation loads the
  whole `job_node` table and every `job_prerequisite` row. Browse already paid this once per page
  (the current node's readiness pill) and now pays it twice, since the subtree needs its own copy
  and the record card still needs `Blockers` detail the subtree does not carry. Both are O(all
  nodes) against a query with dataset-scale budgets (impl plan §0). The fix is to scope the port to
  the ancestor closure its contract already claims, and to let one fetch serve both callers on a
  page; neither belongs in this ADR's change.
