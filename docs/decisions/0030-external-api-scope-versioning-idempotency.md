# ADR 0030: External HTTP API first-release exposure scope, versioning, and idempotency policy

**Status:** Accepted
**Closes:** `docs/plans/2026-07-09-external-http-api-plan.md` §3 items 4–6 (compatibility policy,
exposure boundaries, idempotency policy); supersedes ADR 0024's scope only to the extent of adding
the workflows named below — ADR 0024's rationale for *not* building the full §8.5 surface
speculatively still holds for everything not named here.

## Decision

**Exposure boundaries.** In addition to the existing rate/schedule administration surface (ADR
0024), the first external release adds:

1. **Read-only job context** — browse/search the job tree, node details, ownership, readiness, and
   archive filters.
2. **Work sessions** — start, finish, resume, correct, and list own/authorized sessions.
3. **Prerequisites and achievement** — edit prerequisites, query blockers, update achievement.
4. **Cost reports** — authorized cost summaries/details with rate provenance, scoped so a caller
   never sees another user's session or rate detail without the sensitivity permission that would
   also gate it in the Razor Pages UI.

Structural job commands (create/edit/move/archive/decompose), audit browsing, and account
administration remain Razor-Pages/AdminCli-only for this release. This mirrors why the CLI is the
named consumer (ADR 0029): a remote operator's day-to-day workflow is browsing the tree, logging
work, tracking blockers/achievement, and pulling cost reports — not restructuring the tree, which
stays a deliberately higher-friction, preview-before-commit browser workflow, and not auditing or
administering accounts, which have no concrete non-browser consumer yet.

**API compatibility policy.** The API is **not versioned** for this release. No version
segment or header is added. This is a deliberate choice, not an oversight: §8.4 of the
implementation plan already states the API is versioned "only when a real compatibility
requirement exists," and none exists yet — there is exactly one consumer (the first-party CLI),
built and released alongside the API it calls. Once any client ships that is not deployed in
lockstep with the server (e.g. a mobile app users don't all update immediately), that is the
trigger to introduce versioning, at that time, against the compatibility need that actually
exists then — not preemptively.

Until then, "breaking change" still needs a working definition so the DTOs added under this ADR
don't drift unreviewed:

- Removing a route, field, or enum value, or changing a field's type or meaning, is breaking.
- Adding a new optional field, a new enum value to an open-ended field, or a new route is not
  breaking, provided existing clients ignoring unknown fields/values continue to work.
- Every breaking change to a shipped route requires updating the CLI client proof (§4.5) in the
  same change, since it is the only consumer and the compatibility check for this release.

**Idempotency policy.** Idempotency is decided per mutating command as it is implemented (§4.3),
using the same test ADR 0024 already applied to the rate/schedule endpoints: check whether an
existing database invariant already makes a retried request safe before adding a separate
idempotency-key mechanism.

- Work session start/finish/resume/correct commands need this check explicitly, because (unlike
  the rate/schedule inserts ADR 0024 covers) a session does not obviously have a natural
  uniqueness constraint against a duplicate retry — a retried "finish" against an
  already-finished session must be confirmed to fail safely (not double-apply an effect), and a
  retried "start" must be confirmed either to collide with an existing open session or to be
  reviewed for an idempotency-key mechanism if it does not.
- Prerequisite/achievement edits and cost-report reads are not retry-idempotency concerns in the
  same way: prerequisite edits are expected to be idempotent-by-construction (setting the same
  state twice is a no-op), and cost reports are pure reads.
- Each vertical slice's acceptance checks (§4.3) must record which outcome applied — "protected by
  existing invariant" or "idempotency key added" — rather than leaving it implicit.

## Rationale

- Scoping to browsing, sessions, prerequisites/achievement, and cost reports gives the named CLI
  consumer everything needed for the operational loop it exists to serve, without speculatively
  building the two highest-risk/highest-friction slices (structural commands, account admin) or
  the one with no identified consumer (audit browsing) — consistent with ADR 0024's stance against
  building surface area "just in case."
- Deferring versioning avoids the classic trap of designing a versioning scheme against
  imagined-but-unconfirmed compatibility needs; a single lockstep-deployed consumer has no
  compatibility problem to solve yet, and retrofitting a version segment later is a bounded,
  well-understood migration, not one that gets harder the longer it's deferred at this scale.
- Deciding idempotency case-by-case against actual database invariants (rather than blanket-adding
  an idempotency-key mechanism to every mutating command) keeps the same discipline ADR 0024
  already established, and avoids building generic retry-safety infrastructure the plan doesn't
  otherwise need.

## Consequences

- §4.3's slice order in the external API plan is authoritative for delivery sequencing; this ADR
  fixes which of those slices ship in this release (1, 2, 3, 5) and which do not (4, 6, 7).
- The OpenAPI contract test enumerating the supported route set (successor to
  `The_openapi_document_lists_the_initial_rate_and_schedule_endpoints`) must be updated to include
  exactly these added slices as each is implemented, and must keep failing for any route outside
  this scope.
- If a genuine need for structural commands, audit browsing, or account administration over HTTP
  emerges, it is scoped and reviewed independently, following the same pattern ADR 0024 already
  set for adding to the API surface deliberately rather than by default.
- Work session commands carry an explicit, tracked obligation (not yet resolved by this ADR) to
  determine and document their idempotency treatment before that slice's acceptance checks can be
  called met.
