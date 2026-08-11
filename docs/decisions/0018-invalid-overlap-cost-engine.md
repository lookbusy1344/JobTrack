# ADR 0018: Cost-engine behaviour on an invalid same-(user, LeafWork) overlap

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 17

## Decision

The specification requires costing to include sessions created by corruption or raw writes (they are evidence of recorded labour, spec principle) while separately prohibiting same-user/same-leaf session overlap as an enforced invariant (§6.2 item 7). These two requirements are compatible everywhere the invariant actually holds; they only conflict if the invariant has been violated out-of-band — a raw write bypassing the schema constraints, or an out-of-band SQLite state produced outside the normal write path.

If the materialized cost input nonetheless contains such an overlap when it reaches the pure cost engine, the engine does **not** silently allocate it (which would double-count the same leaf's time across two overlapping sessions) and does **not** silently drop one of the sessions (which would under-report recorded labour, contradicting the "corruption evidence is still evidence" principle). Instead it **throws `InvariantViolationException`** (the shallow `JobTrackException` hierarchy, §7.1) with a stable `ConstraintId` identifying the specific invariant (same-user/same-leaf non-overlap) and the offending session identifiers, so the caller/operator can locate and correct the underlying corruption rather than receive a silently wrong cost figure.

This is a deliberate application of the house style's "every failure throws" rule (§5.1 item 18) to a domain-engine input-validity check, not merely a caller-argument check — the engine treats an invalid input state as exceptional precisely because "quietly compute something plausible" is worse than "refuse and identify the problem" when money is involved.

## Consequences

- A negative/golden test constructs a cost-input fixture containing exactly this overlap (bypassing the normal command path, matching how the corruption would actually arise) and asserts `InvariantViolationException` with the expected `ConstraintId`, per plan §5.1 item 17.
- This is a genuinely different failure category from the ordinary write-path overlap rejection (which happens at write time, before the session is ever persisted, via the GiST exclusion constraint / SQLite trigger, §6.3–§6.4) — the ADR's scenario is specifically "the invariant was already violated before the engine ever saw the data," which only a raw write or out-of-band state can produce.
- Operational runbooks (§9.1) should note `InvariantViolationException` with this `ConstraintId` as a signal to investigate direct-database-access or corruption, not a normal application error to retry.
