# ADR 0041: Work-session visibility is open to every employee; editing stays owner-gated

**Status:** Accepted
**Supersedes:** the self-session view restriction ADR 0032 carried forward into
`WorkSessionAccessPolicy.CanView`.

## Context

`WorkSessionAccessPolicy.CanView(roles, isOwnSession)` let an `Administrator`/`JobManager` read any
work-session list, but restricted a `Worker` to their **own** sessions. That rule was never decided
on its merits: ADR 0032 introduced `CanView` only to split reading from writing when `CanManage`
took on its new node-control meaning, and explicitly recorded that it was "preserving the exact
pre-existing self-session rule" while noting that "nothing in the ownership model motivates gating
*viewing* a session list". It was inherited from the pre-ownership-model design, not justified.

It also sits awkwardly against spec §7.3, whose `Worker` baseline authority begins "**View employees
and job data**" — unqualified. Every restriction in that row concerns *managing* (jobs they own,
their own schedule, not editing another employee's account/schedule/rate). Recorded work is job
data: the job tree, achievement, prerequisites, and readiness are all already viewable by any
employee (`Browse` runs under the broad `AnyEmployee` policy with no ownership gate at all). A leaf's
session history being the one operational read requiring privilege was an inconsistency, not a
policy.

The practical symptom: the leaf sessions panel defaulted to "your own sessions", so the common case —
seeing *what work has been done on this job* — required already knowing whose sessions to ask for,
and was impossible for a Worker looking at a colleague's leaf.

## Decision

**Viewing is open to every employee role; editing is unchanged.**

`WorkSessionAccessPolicy.CanView` drops its `isOwnSession` input and grants the read to any of the
six baseline employee roles:

```csharp
public static bool CanView(IReadOnlyCollection<EmployeeRole> actorRoles) =>
    actorRoles.Any(role => role is EmployeeRole.Administrator
                               or EmployeeRole.JobManager
                               or EmployeeRole.Worker
                               or EmployeeRole.RateManager
                               or EmployeeRole.CostViewer
                               or EmployeeRole.Auditor);
```

`Requester` is excluded, holding no operational job visibility at all (ADR 0033), as is an actor with
no role. This is exactly the `AnyEmployee` set the web already uses to gate job browsing.

`CanManage` is **untouched**: starting, finishing, and correcting a session still require
`Administrator`/`JobManager`, or `Worker` plus control of the leaf's node (own it or an ancestor —
ownership model §4.2, ADR 0032). Seeing another worker's session therefore never implies being able
to edit it.

Alongside, `GetLeafSessionsRequest.WorkedByUserId` becomes **optional**: `null` (the default) returns
every worker's sessions on the leaf, ordered most-recent-first across the union; supplying a worker
narrows to that worker. The whole record of work on a leaf is the entry point; filtering to one
person is the follow-up. `IWorkSessionQueryPort.GetSessionsAsync` takes the same nullable filter and
applies no authorization of its own — `JobQueries` continues to own that from the roles the port
returns alongside.

## Consequences

- The leaf sessions panel (shared by `Browse` and `Work`) now loads every worker's sessions by
  default, renders a "Worked by" column, and offers the worker picker as an "Everyone"-default
  filter. Each row's "Correct" link targets that row's own worker rather than whoever the panel is
  filtered to.
- Row actions (finish/correct) are still rendered unconditionally and enforced by the command, which
  re-evaluates `CanManage` per call. Node control is not derivable in the view without duplicating
  domain authorization where it could diverge, and spec §7.3 is explicit that "hiding a control in
  Razor is not authorization".
- **Breaking API change:** `WorkSessionAccessPolicy.CanView(IReadOnlyCollection<EmployeeRole>, bool)`
  → `CanView(IReadOnlyCollection<EmployeeRole>)`; `GetLeafSessionsRequest.WorkedByUserId` and
  `IWorkSessionQueryPort.GetSessionsAsync`'s `workedByUserId` become nullable. Pre-release, so the
  `PublicAPI.Unshipped.txt` entries are edited in place.
- The external HTTP API's `GET /api/jobs/{nodeId}/sessions` keeps its existing required
  `workedByUserId` query parameter — its contract is unchanged; only who may call it successfully
  widens.
- Tests asserting the old rule were updated to the new one rather than deleted: a Worker reading
  another worker's sessions now succeeds over the page, the cookie API, and a bearer token, and new
  coverage asserts the unfiltered "every worker" mode on both providers plus `Requester` still being
  refused outright.

## Framework Design Guidelines review note

Reviewed against `Framework_Design_Guidelines_Essentials.md` per the public-API-discipline
convention. Dropping a parameter that no longer participates in the decision is preferred to keeping
a vestigial `bool` the implementation ignores — a parameter that cannot change the result is a
misleading API. Making `WorkedByUserId` optional (rather than adding a second request type or a
sentinel) keeps one query with one obvious default, and nullable-as-"no filter" matches the
established shape of `OwnerUserId`/`Limit` elsewhere in `JobTrack.Application`.
