# ADR 0017: Costing read scope versus subtree authorization

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 16

## Decision

Correct concurrency-divisor (`N`) computation requires discovering **all** of a worker's overlapping sessions across the entire database (spec §10.2.2), including sessions on jobs the calling user is not authorized to browse. The engine resolves this by running with an **internal elevated read scope**, used for exactly one purpose — computing each worker's true `N` at each boundary — under the following hard constraints:

- Only **aggregate allocation for the requested jobs** is ever exposed in any result, breakdown line, cost-segment trace (plan §7.2), or diagnostic. A caller authorized only for node X never receives the identity, node, rate, or any other attribute of a foreign session that merely contributed to `N`.
- The elevated scope is an internal implementation detail of the cost-input materialization step (persistence layer, §7.4) and the pure cost engine (§7.2) — it is not a capability exposed through any public API, command, or query, and it is never granted to a caller-supplied authorization context.
- A caller's own authorization (subtree scope, role) governs which jobs' *results* they may request and receive; it never governs or is checked against the internal `N`-discovery read, which is unconditional across the whole database by design.

**Accepted residual information exposure.** A requested job's reported cost being lower than the naïve single-session calculation implies the worker was concurrently busy elsewhere at that time — this is an intentional, documented consequence of correct `N`-based allocation, not a defect. It is accepted rather than mitigated (e.g. by never showing a "single-session-equivalent" comparison figure) because mitigating it would require withholding the correct cost figure itself, which is a worse outcome than the narrow inference it permits.

## Consequences

- A negative test asserts: a caller scoped to node X receives an allocation whose value is influenced by the worker's out-of-scope concurrent sessions elsewhere, yet the same caller's response (including any detail/trace view) never contains those foreign sessions' identifiers, nodes, or rates — this is a required test per plan §5.1 item 16, not optional coverage.
- The cost-input materialization query (§6.5, §7.4) is the only place the elevated scope is exercised; it is implemented once, in the persistence layer, and the pure domain engine (§7.2) never itself performs authorization-scoped filtering — it operates on already-materialized, already-scoped-for-exposure inputs, keeping the engine's determinism and side-effect-freedom intact.
- This residual-exposure acceptance is documented in the threat model (§8.2) as a known, accepted information flow, not omitted — so a future security review does not re-discover it as an undocumented finding.
