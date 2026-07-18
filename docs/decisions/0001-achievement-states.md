# ADR 0001: Canonical achievement states and reopening authority

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 12, §5.5 exit blocker

## Decision

`Achievement` is a closed enum with four values:

- `Waiting`
- `InProgress`
- `Success`
- `Cancelled`
- `Unsuccessful`

Only `Success` satisfies a prerequisite (unchanged from the specification). `Cancelled` and `Unsuccessful` are both terminal, non-success outcomes, distinguished so reporting and audit can tell "withdrawn" work apart from "attempted and failed" work.

### Permitted transitions

- `Waiting -> InProgress -> {Success, Cancelled, Unsuccessful}`
- `Waiting -> {Cancelled, Unsuccessful}` directly (a job may be cancelled or marked unsuccessful before work starts)
- Any terminal state (`Success`, `Cancelled`, `Unsuccessful`) may be **reopened** back to `Waiting`.

### Reopening authority

Reopening a terminal state requires the Job manager or Administrator role and:

- a mandatory reason string, recorded in the audit event alongside the previous and new state;
- re-evaluation of the node's own readiness on the next transition out of `Waiting` (reopening does not retroactively re-validate anything upstream — a dependent job whose prerequisite regresses from `Success` is handled by the existing "blocked sessions" rule, not by this ADR);
- no second-person approval, consistent with the existing session-correction authorization model (spec §"historical sessions").

Reopening a branch has no direct effect — branch achievement is always derived recursively from leaf state (spec §5.2) and is never itself a stored, reopenable value.

## Consequences

- Schema: `achievement_status` is a constrained enum/lookup with exactly these five values; no legacy single-character codes.
- The audit event schema must carry previous/new achievement plus a reason for every transition, not only for reopening.
- Domain-layer exhaustive `switch` expressions over `Achievement` must handle all five cases with no `default` arm (house style, §5.1 item 18).
