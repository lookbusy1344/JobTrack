# ADR 0032: Owner-gated work-session recording

**Status:** Accepted
**Closes:** `docs/ownership-model.md` §4.2; `docs/plans/2026-07-11-job-node-ownership-and-work-authorization.md`
Stages 3–4; supersedes `jobtrack_spec_codex.md` `:296`'s self-session wording; breaking change to
`JobTrack.Domain.Authorization.WorkSessionAccessPolicy` (public surface past the library gate, ADR
0013's compatibility policy).

## Context

`WorkSessionAccessPolicy.CanManage(actorRoles, isOwnSession)` gated starting, finishing, and
correcting a work session purely on self-vs-others: a Worker could act on a session if and only if
`actorId == workedByUserId`. Node ownership (ADR 0031) played no part. Combined with the legacy
`0..1` work-record cardinality this was originally written against, "own session" used to be an
adequate proxy for "authorized to record here" — but the current schema allows many `work_session`
rows per leaf, each independently carrying its own `worked_by_user_id`, so the self-check no longer
tracked who actually controlled the node being worked. Any authenticated Worker could record their
own time against **any** node in the system, owned by them or not — too open once node ownership
became the intended axis of control (ADR 0031).

## Decision

`WorkSessionAccessPolicy.CanManage` changes signature from `(roles, isOwnSession)` to
`(roles, actorControlsNode)`, where `actorControlsNode` is the same `controls(actor, node)` predicate
ADR 0031 defines for job-node structural commands — computed via the identical ancestor-owner walk
(`GetAncestorOwnerIdsAsync`), reused rather than duplicated, so the job-node and work-session ports
can never drift to different control sets for the same node. This governs `StartSessionAsync`,
`FinishSessionAsync`, and `CorrectSessionAsync` identically on both providers.

Consequences of the new predicate:

- A controlling owner (or Administrator/JobManager, unconditionally as before) may record a session
  for **any** `worked_by_user_id` — entering time on behalf of other people is now a first-class
  owner capability, not something the policy previously had any notion of.
- A Worker who controls nothing on the tree may record **no** work there, not even their own — they
  must first pick up a node (ADR 0031 §4.3) or be given one they own.
- An unassigned (`NULL`-owned) node is not directly workable by a plain Worker: `controls` is false
  for everyone until claimed. Administrator/JobManager may still record on it directly, since their
  bypass never depended on `controls`.

**Viewing a session list is a separate, unaffected concern.** `GetLeafSessionsAsync`/
`GetActiveSessionsAsync` previously reused the same `CanManage` self-check to gate "may I see this
worker's sessions" — that is a different axis (is this list the caller's own) than node ownership,
and the plan never asked for it to become ownership-gated. Rather than silently repurpose
`CanManage`'s new meaning for viewing too, `WorkSessionAccessPolicy` gains a second predicate,
`CanView(roles, isOwnSession)`, preserving the exact pre-existing self-session rule
(Administrator/JobManager unrestricted; Worker only their own list). The two read call sites now
call `CanView`, not `CanManage`.

## Rationale

- Reusing `controls(actor, node)` rather than inventing a parallel notion keeps exactly one place
  (`JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync`) responsible for "who controls this node," so
  a future change to the cascade rule (e.g. how `NULL` ancestors are skipped) cannot update
  structural authorization without also updating work-session authorization, or vice versa —
  precisely the risk the implementation plan's own watch-list called out.
- Splitting `CanManage`/`CanView` rather than overloading one predicate for both "may write" and
  "may read" avoids a false coupling: nothing in the ownership model motivates gating *viewing* a
  session list by node control, and doing so anyway would have been an unreviewed, undocumented
  policy change smuggled in under an unrelated refactor.
- The breaking signature change is accepted, not deprecated-and-parallel, because ADR 0013's
  compatibility policy treats this as a pre-1.0-equivalent in-repo breaking change: there is exactly
  one library, `JobTrack.Web`'s Razor Pages and the external HTTP API are the only consumers, and
  both were updated in the same stage that changed the signature — there is no external consumer to
  stage a deprecation window for.

## Framework Design Guidelines review note

Reviewed against `Framework_Design_Guidelines_Essentials.md` per the public-API-discipline
convention (impl plan's "review additions... before and after implementation"): the rename from
`isOwnSession` to `actorControlsNode` is a same-shape, same-arity signature change (still
`(IReadOnlyCollection<EmployeeRole>, bool) -> bool`), so no new overload resolution ambiguity or
parameter-ordering concern arises; the boolean parameter's name change is itself the documentation
fix this ADR requires (a stale `isOwnSession` name on a parameter that no longer means "is this the
caller's own session" would violate the guidelines' naming-reflects-meaning principle). No other
public member of `JobTrack.Domain.Authorization` was touched beyond the new `CanView` addition,
which is purely additive.

## Consequences

- `PostgreSqlWorkSessionCommandPort`/`SqliteWorkSessionCommandPort`'s `AuthorizeOrThrowAsync` takes
  the leaf's `JobNodeId` instead of `workedByUserId`, computing `controls` the same way the job-node
  ports do; `FakeWorkSessionCommandPort` mirrors this for `JobTrack.Application.Tests`.
- Audit events continue to record `request.Context.Actor` as the actor and `worked_by_user_id` as
  the worker — on-behalf-of recording never blurs those two fields, regardless of who controls the
  node.
- `jobtrack_spec_codex.md` `:296` is updated: "Workers may correct their own historical sessions"
  becomes owner-gated wording — a controlling Worker may record/correct for any worker on a node
  they control; a non-controlling Worker may record no session there, own or otherwise, until they
  control the node.
