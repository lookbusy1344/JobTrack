# Plans index

One line per plan, its current status, and what it's about. Update this table whenever a plan's own
status block changes — do not let this index drift from the plans it points to.

Reviewers: a plan marked `Proposed` here is **not** accepted evidence for any phase gate (§6.7,
§7.5, §8.7) or ADR acceptance. Only a plan's own status block, or an ADR with `Status: Accepted`, is
authoritative — this table is a navigation aid, not a second source of truth.

| Plan | Status | Subject |
|---|---|---|
| [`jobtrack_impl_plan.md`](jobtrack_impl_plan.md) | Proposed 4 (active) | Delivery plan: phase gates, milestone sequence, current phase status — the authoritative phase tracker. |
| [`2026-07-08-fix-plan.md`](2026-07-08-fix-plan.md) | Closed | Early post-review fix plan. |
| [`2026-07-09-external-http-api-plan.md`](2026-07-09-external-http-api-plan.md) | Proposed | Defines the external HTTP API's scope, client trust model, and acceptance bar (ADR 0029/0030). |
| [`2026-07-09-overlapping-cost-scale-plan.md`](2026-07-09-overlapping-cost-scale-plan.md) | Implemented | Overlapping-session cost-engine scale work. |
| [`2026-07-09-phase-gate-evidence-plan.md`](2026-07-09-phase-gate-evidence-plan.md) | Implemented | Closed traceability/ADR evidence gaps for the Phase 1-3 gates (ADR 0025/0026/0027). |
| [`2026-07-10-external-http-api-remediation-plan.md`](2026-07-10-external-http-api-remediation-plan.md) | Implemented | Remediation of the external HTTP API review findings (pagination, OpenAPI contract, auth boundary evidence). |
| [`2026-07-11-client-requester-intake-plan.md`](2026-07-11-client-requester-intake-plan.md) | Core slice complete (Stages 1-8); Stage 9 closed by ADR 0034 | Requester self-service intake, holding areas, and progress tracking (ADR 0033/0034). |
| [`2026-07-11-job-node-ownership-and-work-authorization.md`](2026-07-11-job-node-ownership-and-work-authorization.md) | Implemented | Nullable node ownership, unassigned pool, pickup, and owner-gated work-session authorization (ADR 0031/0032). |
| [`2026-07-11-postgresql-column-type-remediation-plan.md`](2026-07-11-postgresql-column-type-remediation-plan.md) | Proposed | PostgreSQL column-type remediation. |
| [`2026-07-11-security-review-remediation-plan.md`](2026-07-11-security-review-remediation-plan.md) | Remediated | Closed all seven security-review findings (§2.1-§2.7). |
| [`2026-07-12-comprehensive-code-review-remediation-plan.md`](2026-07-12-comprehensive-code-review-remediation-plan.md) | In progress | §2.1-§2.15 implemented; §3 delegated plans unchanged. |
| [`2026-07-12-end-user-testing-readiness-remediation-plan.md`](2026-07-12-end-user-testing-readiness-remediation-plan.md) | Implemented | Readiness/evidence gaps closed before broad end-user testing: staff holding-area traceability, stale plan status, and a canonical UAT seed. |
| [`2026-07-12-temporal-representation-hardening-plan.md`](2026-07-12-temporal-representation-hardening-plan.md) | Implemented | Temporal-representation hardening across schedule/identity time-zone handling. |
| [`2026-07-12-derived-node-kind-plan.md`](2026-07-12-derived-node-kind-plan.md) | Implemented | Drop stored `job_node.kind_id`; derive Root/Branch/Leaf from structure at read time, single "create child" action (ADR 0035). |
| [`2026-07-14-security-audit-remediation-plan.md`](2026-07-14-security-audit-remediation-plan.md) | Remediated | Fresh security audit remediation: requester account self-service, partitioned login/2FA throttling, authentication audit events, and self-service 2FA credential-transition consistency. |
| [`2026-07-15-browse-multi-level-subtree-plan.md`](2026-07-15-browse-multi-level-subtree-plan.md) | Implemented | Multi-level Browse tree: bounded-depth adjacency-list subtree query, cost roll-up read-out, and the computed interval visualization (DB→library→API→web, TDD; ADR 0039/0040). |
| [`2026-07-19-fresh-eyes-code-review-remediation-plan.md`](2026-07-19-fresh-eyes-code-review-remediation-plan.md) | Implemented | Fresh-eyes findings spanning civil-time correctness, injected clocks, authentication-audit integrity, bounded queries, PRG-safe PAT delivery, and cost-query fan-out. |
| [`2026-07-21-browse-sessions-navigation-and-closure-plan.md`](2026-07-21-browse-sessions-navigation-and-closure-plan.md) | Implemented | Makes Browse the job-workflow centre, exposes Sessions consistently, handles concurrent workers without collapsing them, and prevents new sessions on terminal or archived leaves; final 2,634-test solution suite verified. §4.1/§4.3's "Sessions only on `/Jobs/Work`" is superseded in part by ADR 0046 (Browse's own leaf detail view now embeds the panel too). |
| [`2026-07-22-unified-leaf-workflow-plan.md`](2026-07-22-unified-leaf-workflow-plan.md) | Implemented (ADR 0045); all provider races, unified Work-only interactions, and dual-provider axe/keyboard/reflow evidence complete | Unifies Sessions and achievement around atomic one-click completion and compact audited reopen-and-start workflows. |

## Review checklist item

When citing a plan as accepted evidence for a phase gate or milestone, confirm the plan's own status
block (not just this table, which can lag) says `Implemented`, `Closed`, `Remediated`, or points to
an ADR with `Status: Accepted`. A plan whose own status block still says `Proposed` is not acceptable
evidence that its work is done, regardless of what the traceability catalogue or source code appears
to show.
