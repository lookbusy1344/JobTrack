# ADR 0004: Specification precedence and secondary-spec differences

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 1

## Decision

`jobtrack_spec_codex.md` is the normative specification. `jobtrack_spec_claude.md` supplies implementation detail only where consistent with it. Any conflict is resolved in favour of Codex and recorded here before code is written, rather than inferred silently during implementation.

Known secondary-spec differences, reconciled explicitly (plan §1):

- **Overtime rate.** An additive schedule exception may carry an explicit hourly rate, taking highest precedence within its interval; an unpriced additive exception uses ordinary precedence. Overlapping explicitly priced additive exceptions for one user are prohibited. Both specs are consistent here once the "explicit rate has highest precedence" reading is fixed.
- **Achievement.** Only canonical `Success` satisfies a prerequisite; an ordering threshold does not redefine success. The secondary spec's illustrative `Achievement` enum sketch (`{ …, NotAchieved, Partial, Success }`) is non-normative — see ADR 0001 (`0001-achievement-states.md`).
- **Blocked sessions.** Recorded sessions remain costable and count in concurrency allocation after prerequisite regression. Both specs already agree; the secondary spec's framing of this as an override of Codex is mistaken — no divergence exists.
- **Canonical query boundary set (§6.5).** The secondary spec's Appendix C.4 rate-resolution boundary sketch omits node-override ancestor-chain boundaries. Codex's exhaustive boundary set (session start/end, schedule-interval edges, exception edges, `user_cost_rate` edges, and `node_rate_override` edges for every ancestor holding an override for the worker) is authoritative; Appendix C.4 is incorrect on this point and is not implemented as written.
- **Illustrative C# surface and DDL.** Appendix sketches such as `double AllocatedHours` are non-normative and contradicted by ADR 0009 (`0009-decimal-precision-and-allocation.md`) (no `double` on the duration/money path); the reviewed API and schema are authored fresh against the frozen decisions in this document set, not seeded from those sketches.

## Consequences

- A future spec conflict not already listed here must be resolved against Codex and recorded as a new ADR (or an addendum to this one) before the affected code is written — it is not inferred ad hoc during implementation.
- Product-semantic questions that neither spec resolves unambiguously go to the product owner and get their own ADR (see ADR 0001 (`0001-achievement-states.md`), ADR 0002 (`0002-penny-reconciliation.md`), ADR 0003 (`0003-historical-correction.md`)) rather than being decided by document precedence alone.
- Code review and PR descriptions that touch a point covered here should cite the relevant ADR rather than re-litigating the spec conflict.
