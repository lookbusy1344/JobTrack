# ADR 0034: Requester acceptance, notes, and the request detail projection

**Status:** Accepted
**Closes:** `docs/plans/2026-07-11-client-requester-intake-plan.md` §7's open product decision;
`/Requests/{id}` and its API equivalent, deferred at ADR 0033 Stage 6/§9.
**Depends on:** ADR 0033.

## Context

ADR 0033 shipped requester submission, the `job_request` anchor, staff-triage move, and the flat
`/Requests` list, but left the request detail page open, because three questions had no answer yet:

1. Plan §7 lists a public status vocabulary (`Submitted`/`Accepted`/`InProgress`/`Waiting`/
   `Completed`/`Cancelled`) and explicitly flags `Accepted` as undecided — inferred from
   assignment/move, or a separate persisted staff action.
2. Plan §4's schema list has no notes/comments table at all — an outright gap, not a design choice.
   `/Requests/{id}` cannot show "notes from staff" without one.
3. The plan's own text says notes are "posted by staff or by the requester" (§2, §8), but nothing
   fixed who can write which kind, or how visibility is expressed.

## Decision

- **`Accepted` is an explicit, persisted staff action**, not inferred from assignment or a holding-
  area move. `job_request` gains nullable `acknowledged_at`/`acknowledged_by_user_id`, set only by a
  new `IJobRequestCommandPort.AcknowledgeAsync`, authorized the same way as `MoveAsync`
  (`JobNodeAccessPolicy.CanManage` against the node's ancestor-owner set) and audited as
  `"acknowledge-request"`.
- **A new `job_request_note` table** carries both staff and requester notes: `id`, `job_node_id`
  (the request's anchor node — the same node the subtree is rooted at, so a decomposed request's
  notes stay attached to the one place both requester and staff already look), `author_user_id`,
  `content`, a single `is_visible_to_requester` boolean, `created_at`, `row_version`. Notes are
  append-only (no update/delete), mirroring `audit_event`'s immutability triggers (schema version
  0012) for the same reason: a note is a record of what was said, not a mutable field.
- **Both staff and the requester can write notes** through one `IJobRequestCommandPort.AddNoteAsync`,
  which branches on the actor's relationship to the request rather than exposing two commands:
  a requester-authored note is authorized by the existing, previously-unused
  `RequesterAccessPolicy.CanCommentAsRequester` and is always `is_visible_to_requester = true` (a
  requester cannot post a private note to themselves); a staff-authored note is authorized by
  `JobNodeAccessPolicy.CanManage` and its visibility is caller-supplied.
- **Visibility is a single boolean**, not a richer ACL/enum. Nothing else in this schema expresses
  per-role visibility with more than a binary split, and no requirement here calls for more than
  "requester can see this" vs. "staff-only".
- **The requester-safe subtree query is unbounded**, like every other recursive hierarchy query in
  `JobNodeHierarchyQueries.cs` (`GetAncestorOwnerIdsAsync`, `GetSubtreeAchievementsAsync`, the
  PostgreSQL `job_node_descendants` function). None of them impose a depth or row cap; all rely on
  the DB-enforced cycle-free invariant (schema version 0005) for termination. A new bound here would
  be an unjustified deviation from that established convention.
- **Public status derivation** is a new pure `RequesterStatusCalculator` (`JobTrack.Domain`), taking
  the acknowledged flag and the request subtree's leaf achievements as input, in this precedence
  (most-decided first): subtree fully achieved (`AchievementCalculator.IsAchieved`) → `Completed`;
  else every leaf's achievement is terminal-negative (`Cancelled`/`Unsuccessful`) and none pending →
  `Cancelled`; else any leaf `InProgress` → `InProgress`; else any leaf `Waiting` (`LeafWork` exists,
  unstarted) → `Waiting`; else `acknowledged_at` set → `Accepted`; else → `Submitted`. This reflects
  progress across the whole subtree once staff decompose the request (plan §7: "computed from that
  node's subtree, not from only the current direct leaf"), not just the anchor node.
- A new combined coarse-admission policy, `JobTrackPolicyNames.RequestDetailAccess`
  (`RequireRole(Requester, Administrator, JobManager, Worker)`), gates the detail/comments endpoints,
  since `RequireAuthorization` ANDs every named policy and the endpoint must be reachable by two
  disjoint role sets. The authoritative check remains inside the operation
  (`RequesterAccessPolicy.CanView`/`CanCommentAsRequester`, `JobNodeAccessPolicy.CanManage`), per the
  existing "coarse admission only" convention (ADR 0033, `Program.cs`).

## Rationale

- Explicit acceptance gives requesters an honest "seen by IT" signal distinct from "still sitting in
  intake", which is what plan §7 itself already preferred; inferring it from assignment or a move
  would conflate "staff looked at it" with "staff did something structural", producing a false
  `Accepted` for a request an owner glanced at and left untouched.
- Branching one `AddNoteAsync` on actor relationship, rather than two separate commands, keeps a
  single audit trail (`"add-request-note"`) and a single authorization decision point per note,
  instead of duplicating the write path for what is structurally the same row.
- Rooting notes at the request's anchor node rather than at whichever leaf staff happen to be working
  keeps the notes thread coherent across decomposition — a requester should not need to know which
  sub-job a note was attached to.

## Consequences

- `docs/database-entities.md` gains `job_request.acknowledged_at`/`acknowledged_by_user_id` and the
  new `job_request_note` table.
- `docs/traceability/test-catalogue.md`'s pending `TC-DB-REQ-004`, `TC-APP-REQ-003`,
  `TC-WEB-REQ-001`, and `TC-WEB-REQ-004` rows are filled in as this slice's tests land; new IDs are
  added for acceptance- and note-specific coverage that those placeholders didn't anticipate.
- `docs/plans/2026-07-11-client-requester-intake-plan.md` gains a Stage 9 describing this slice and
  its deferred-item note is marked resolved.
