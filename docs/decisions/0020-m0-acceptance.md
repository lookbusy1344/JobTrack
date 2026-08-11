# ADR 0020: M0 (Foundation) acceptance

**Status:** Accepted
**Closes:** Implementation plan §5.5, §13 steps 1–5

## Decision

M0 is formally accepted. Every §5.5 foundation exit criterion is satisfied:

| Criterion | Evidence |
|---|---|
| Solution builds from a clean checkout with the pinned SDK | `dotnet build JobTrack.slnx -warnaserror`, `dotnet format JobTrack.slnx --verify-no-changes`, and `dotnet test JobTrack.slnx` all pass (0 warnings, 0 errors; all test projects are still empty scaffolds pending Phase 1 feature work, which is expected and correct at this stage) |
| Test categories and timeout budgets are documented | `docs/traceability/test-catalogue.md` §2 |
| The requirement traceability skeleton exists | `docs/traceability/test-catalogue.md` §1, §3 (identifier scheme and spec-clause traceability table) |
| De-risking spikes demonstrate the required PostgreSQL behaviour under concurrent writes, and the time model handles the documented DST cases deterministically | `docs/traceability/spike-report.md`; all six §5.3 spikes pass against a real local PostgreSQL 18 instance under simultaneous independent connections, and the DST spike passes against pinned TZDB `2026b` |
| Performance and scale budgets are defined and recorded | `docs/traceability/performance-budgets.md` |
| All semantic and technology decisions needed for database design are accepted | ADRs 0001–0019 close every §5.1 item |
| The three product-semantic decisions are closed with the product owner | ADRs 0001, 0002, 0003 (achievement states, penny reconciliation, historical correction) |

## Consequences

- Per plan §1 and §13, M1 schema-version work (§6.2 slice order, starting with schema-version
  metadata and Identity storage) may now begin. No M1 schema version is frozen retroactively — this
  ADR is the formal gate that unblocks that work, not a description of work already done.
- Two findings surfaced by the spikes were folded back into the affected ADRs before this
  acceptance was recorded, per the "spikes retire risk and inform the frozen decisions" principle
  (§5.3): ADR 0012 gained a proven "prerequisite-graph writes" lock domain, and the persistence
  error-translation requirement noted in the spike report (§7.4: map GiST-overlap deadlocks and
  exclusion violations to the same public error category) is carried forward as an implementation
  obligation for the persistence phase, not merely a spike footnote.
- Should a Phase 1 slice later expose a defect in an M0 decision (a wrong ADR, an unachievable
  performance budget, a traceability gap), plan §1's rule applies: the correction is made in the
  earlier phase (the relevant ADR or traceability document is revised) and the affected gate
  re-passes — it is not patched over in Phase 1 schema or Phase 2 library code.
- This acceptance record is dated to the commit that closes it; a future M0 revision (e.g. adding a
  new ADR after a Phase 1 finding) does not retroactively invalidate this ADR's history — it is
  recorded as a new decision or an explicit amendment to the affected ADR, per ADR 0004's
  precedent for handling spec/decision conflicts.
