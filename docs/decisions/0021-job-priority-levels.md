# ADR 0021: Job priority levels

**Status:** Accepted
**Closes:** Implementation plan §6.2 item 4 (`job_node.priority_id` reference data)

## Decision

`priority` is a closed reference table with four values, in escalating order:

- `Low`
- `Medium`
- `High`
- `Urgent`

Every `JobNode` requires a priority (spec §4.1: "Priority | priority identifier | Required"), but
neither spec document nor any prior ADR enumerated the concrete level set — §11 lists `priority`
among the required reference tables (same category as `achievement_status`, and historically
`node_kind` before ADR 0035 removed stored node-kind state) without naming its rows. This ADR closes
that gap with the smallest scheme that still distinguishes
"needs attention soon" from "business as usual," consistent with the four/five-value size of the
other closed enums (`achievement_status` has five; `node_kind` had three before ADR 0035).

## Consequences

- Schema: `priority` is seeded with exactly these four rows (ids 1-4, in the order above), the same
  shape as `achievement_status` and the then-current `node_kind` from schema slice 1. ADR 0035 later
  removed `node_kind`; `priority` remains stored reference data.
- No ordering/comparison semantics beyond storage are specified here — how priority affects
  scheduling, sorting, or UI treatment is an application/front-end concern for a later phase, not
  part of this ADR.
- If a fifth level or a materially different scheme is ever needed, it requires a new ADR rather
  than an ad hoc migration — the same discipline already applied to `achievement_status` (ADR 0001).
