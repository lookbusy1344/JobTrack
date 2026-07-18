# End-user testing readiness remediation plan

**Date:** 2026-07-12  
**Status:** Implemented. §2.1 (staff holding-area traceability — `TC-APP-REQ-001`, `TC-DB-REQ-005`,
`TC-APP-REQ-003`, `TC-WEB-REQ-001` all closed with fully qualified tests), §2.2 (stale plan status
reconciled, `docs/plans/README.md` index added), and §2.3 (`samples/JobTrack.UatSeed` synthetic UAT
seed plus its smoke test) are all closed. No phase 1-3 traceability row remains pending except the
explicitly-noted rate-limiting duplicate-ID row, which points to already-implemented coverage. Full
solution suite passes (1,931 tests, 0 failures) as of the closing commit.
**Scope:** Fresh review of the current implementation against `jobtrack_impl_plan.md` phases 1-3,
with Phase 4 production hardening intentionally out of scope except where it blocks credible
end-user testing.

## 1. Review baseline

The core layering is in good shape:

- database contracts, provider ports, and schema scripts are separated from the application facade;
- the reusable library exposes `IJobTrackClient` and immutable request/result contracts;
- the web and external HTTP API call `IJobTrackClient`; provider references are confined to host
  composition;
- `JobTrack.Identity` is an authentication adapter, not a domain persistence path;
- `samples/JobTrack.ExternalApiClient` remains an HTTP-only proof with no `JobTrack.*` project
  references; and
- `JobTrack.ArchitectureTests` passed in this review (`20/20`) without running the full suite.

The implementation is therefore architecturally clean enough to proceed toward end-user testing.
The gaps below are not a reason to redesign the system. They are readiness/evidence gaps that would
make a user test confusing or hard to trust.

## 2. Substantial gaps to close before broad end-user testing

### 2.1 Staff-side requester intake is not yet fully closed in traceability

Requester self-service, request detail, notes, acknowledgement, ownership, pickup, and the external
API are implemented. The remaining visible gap is the staff operating path around holding areas:
`docs/traceability/test-catalogue.md` still has pending rows for:

- `TC-APP-REQ-001` — requester policy tests at the application/domain-policy layer;
- `TC-DB-REQ-005` / `TC-APP-REQ-003` / `TC-WEB-REQ-001` — staff holding-area queue view,
  assignment evidence, decomposition-preserves-anchor evidence, and requester context on staff
  detail pages.

For end-user testing, this matters because a requester can submit and monitor a request, but a staff
tester needs a clearly supported queue/triage path to pick up, assign, move, or decompose that
request while preserving the requester-visible anchor.

**Remediation:**

1. Add the missing application-policy tests for requester exclusion from pickup/manage/work/rate/
   cost/audit and inclusion only in requester-safe submit/view/comment paths.
2. Add staff queue tests that prove a holding-area job appears in the operational staff view without
   exposing requester-only data.
3. Add assignment and decomposition tests showing the `job_request` anchor remains attached to the
   original request node and requester detail continues to show the safe subtree after staff action.
4. Add the smallest UI affordance needed for staff testers to identify requester context while
   triaging, using the existing Console design primitives.
5. Replace the pending traceability rows with fully qualified tests.

### 2.2 Gate and plan evidence is stale enough to mislead reviewers

Several plans still advertise stale statuses even though source, ADRs, and traceability show the work
has moved on:

- `2026-07-10-external-http-api-remediation-plan.md` says `Proposed`, while the traceability
  catalogue maps pagination, OpenAPI route-set checks, bearer problem details, route identity,
  idempotency, cancellation, and operational-quality tests.
- `2026-07-11-job-node-ownership-and-work-authorization.md` says `Proposed — not started`, while
  `docs/ownership-model.md`, ADRs 0031/0032, nullable `owner_user_id`, pickup, and owner-gated work
  are implemented.
- `2026-07-09-phase-gate-evidence-plan.md` says `Proposed`, while ADRs 0025/0026/0027 accept M3/M6/M8.

This is not cosmetic for this project: the plan itself is the phase-gate authority. Stale plan status
causes false positives when asking whether phases 1-3 are complete.

**Remediation:**

1. Update each stale plan's status block to reflect implemented, partially implemented, or superseded
   state, with commit/test references where already known.
2. Move any genuinely remaining item out of stale plans into this plan or the requester-intake plan,
   so there is a single current source for open phase 1-3 work.
3. Add a short `docs/plans/README.md` index listing active, implemented, and superseded plans.
4. Add a lightweight documentation check, or at minimum a review checklist item, that no phase-gate
   plan marked `Proposed` is cited as accepted evidence.

### 2.3 End-user testing lacks a canonical synthetic scenario

The README documents local PostgreSQL/SQLite startup and bootstrap, and the automated tests build
rich fixtures. There is not yet a canonical, non-PII UAT seed that creates the roles and workflow
state a human tester needs: requester, job manager, worker, rate/cost roles, department, holding
area, unassigned request, assigned work, prerequisite blocker, active session, cost-reportable work,
and audit history.

Without this, end-user testing will either start from an empty bootstrap admin account or rely on
ad hoc manual setup. That slows testing and makes feedback hard to reproduce.

**Remediation:**

1. Add a development-only synthetic seed command or documented script that runs through public
   library/API paths, not raw table writes except where schema deployment/bootstrap already requires
   them.
2. Seed only fake names, emails, departments, and job content.
3. Cover the minimum test journeys: requester submit/detail/comment, staff queue/acknowledge/move,
   pickup, work-session start/finish/correct, prerequisite/achievement, rate setup, cost report,
   and audit browse.
4. Document exact reset steps for SQLite and PostgreSQL local UAT databases.
5. Add a smoke test or sample-client proof that the seed can be applied to a fresh database.

## 3. Completion criteria

This plan is closed when:

- no phase 1-3 row in `docs/traceability/test-catalogue.md` is pending unless explicitly deferred by
  an accepted ADR;
- active plan statuses agree with implemented source and accepted ADRs;
- staff and requester testers can exercise the complete request lifecycle from a synthetic seeded
  environment; and
- targeted tests for the changed database/library/web areas pass under `gtimeout`.

Do not run the full solution suite for each slice. A full run is useful only once this readiness
plan is complete or immediately before a formal release candidate.
