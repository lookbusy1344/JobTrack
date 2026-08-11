# ADR 0012: PostgreSQL lock keys and structural-operation serialization

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 9

## Decision

PostgreSQL advisory locks are used only where concurrent tests (§5.3, §6.6) show that constraint deferral alone is insufficient or produces unacceptable contention — deferred constraint triggers and GiST exclusion constraints (§6.3) are the default enforcement mechanism; advisory locks are the deliberate exception, not the default.

**Lock key allocation.** Advisory locks use `pg_advisory_xact_lock(key)` (transaction-scoped, auto-released on commit/rollback — never session-scoped locks, which would survive a connection-pool handoff). Each logical lock domain gets a distinct, named 64-bit key derived deterministically from a fixed namespace constant and the contended entity's `bigint` id (e.g. `hashtext('jobtrack:subtree-move') XOR job_node_id`, or an equivalent documented construction) — never a bare literal chosen ad hoc per call site. The known lock domains, established as this ADR's initial set (extended only with a documented addition, not silently):

- **schema deployment** — one fixed, well-known key (no entity id), serializing concurrent deployment-tool runs (§6.1, ADR 0011 (`0011-schema-deployment-versioning.md`));
- **subtree move / decomposition** — keyed on the moving node's id, and, per the deterministic-ordering rule below, on every ancestor id up to the root when a move could contend with a concurrent ancestor-level operation;
- **bootstrap** — one fixed, well-known key, serializing concurrent first-administrator bootstrap attempts alongside the partial unique index guard (see ADR 0015 (`0015-bootstrap-state-machine.md`));
- **prerequisite-graph writes** — one fixed, well-known key (no entity id), serializing every `job_prerequisite` edge insert. **Proven necessary, not merely assumed:** the §5.3 spike (`spikes/sql/02-prerequisite-cycle.sql`) showed that a deferred constraint trigger doing a recursive-CTE reachability check, with no lock, lets two concurrent edge inserts that are each individually acyclic (e.g. `A→B` and `B→A` submitted at the same time) both commit and jointly create a cycle — each transaction's check only sees committed data, not the other's in-flight insert. Serializing all edge writes behind this lock closes the race (see `docs/traceability/spike-report.md` §2 for the full before/after trace).
- **leaf session closure** (ADR 0044) — keyed on the leaf's `job_node_id`, serializing `work_session` start/reactivation against a terminal achievement transition or a leaf archive on the same leaf. Necessary for the same write-skew reason as the two domains above: "is any session active" and "commit the closure" must be evaluated as one serialized unit per leaf, not as an unlocked read followed by an independent write on a different table.

**Deterministic lock ordering.** Where an operation must hold more than one advisory lock simultaneously (e.g. a subtree move touching both the source and destination parent), locks are acquired in a single, fixed total order — ascending numeric key order — never in an order derived from request arrival or entity-creation order. This is the standard deadlock-avoidance discipline and is asserted by a concurrent test that deliberately issues opposing-order requests and confirms no deadlock occurs (only serialization).

## Consequences

- Every advisory-lock call site is documented (a comment stating the lock domain and why deferred constraints alone were insufficient) and covered by the corresponding §6.6 race test (opposing moves, concurrent same-subtree edits).
- Advisory lock usage is reviewed as synchronization code (§6.6 "review triggers as synchronization code" applies equally here), not sprinkled defensively — a new call site requires evidence from a failing concurrent test, matching the "spikes retire risk" principle (§5.3).
- Lock keys are defined once, in one internal PostgreSQL-provider constants file, not restated as literals at each acquisition site (no-magic-numbers convention).
