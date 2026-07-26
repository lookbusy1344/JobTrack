# Multi-instance Web Deployment Plan

**Date:** 2026-07-26
**Status:** Proposed — no work item here is started. This plan is a design record for future work, not
evidence for any phase gate.
**Scope:** What must change before `JobTrack.Web` can run as two or more instances behind a load
balancer against one shared PostgreSQL database. Covers the five in-process stores, the
data-protection key ring, load-balancer integration, rolling-deploy schema compatibility, and the
test/evidence bar. Explicitly out of scope: containerization, orchestration, database failover,
read replicas, and any horizontal scaling of PostgreSQL itself.

**Prerequisite decision.** ADR 0014 (`Status: Accepted`) fixes single-server topology as policy — "no
containers, no orchestration, no multi-node coordination, no distributed cache". **Nothing in this
plan may be implemented until a superseding ADR is accepted.** §7 specifies that ADR. Treat this
document as the design that ADR would adopt, not as authorization to build it.

## 1. Current position

The current codebase is *nearly* multi-instance-ready, because the durable state is already
correctly placed:

- **All domain state is in PostgreSQL**, reached only through `IJobTrackClient`. Neither the Razor
  Pages layer nor the HTTP API touches the database directly (CLAUDE.md, impl plan §1).
- **Compound writes are single ACID transactions** committing once
  (`JobNodeWriteExceptionTranslation.RunAndCommitAsync`), so an interleaved write from another
  instance sees a consistent state or nothing.
- **Concurrency control is database-level, not process-level.** Optimistic concurrency uses explicit
  `version` columns checked inside the transaction (`CheckVersionOrThrow`,
  `DbUpdateConcurrencyException` translation); serialization where a version column is insufficient
  uses PostgreSQL advisory locks (ADR 0012, `PostgreSqlLockKeys`,
  `PrerequisiteReadinessSerialization`). Advisory locks are held by the database, not the process,
  so they serialize correctly across instances **as-is**. No change needed.
- **Authentication carries no server-side session.** Cookie and bearer PAT are both stateless per
  request; `SecurityStampValidatorOptions.ValidationInterval` is `TimeSpan.Zero`, so revocation
  propagates through the database on every request rather than through an in-process cache. A user
  disabled on instance A is refused by instance B on the next request.
- **No hosted services, background services, or timers.** Nothing would double-fire or race if
  scheduled on every instance.
- **No migration-on-startup.** Schema deployment is a separate `JobTrack.Database` invocation guarded
  by `PostgreSqlDeploymentLockStrategy` (an advisory lock), so two concurrent deploys already
  serialize.

The gap is entirely in `JobTrack.Web`'s own convenience/security-control state, plus the ASP.NET Core
data-protection key ring.

**Provider constraint:** `Database:Provider` must be `postgresql`. The SQLite provider
(`JobTrackSqlite.Create` against a local file) is single-node by construction — ADR 0014 already
calls it "a mutually exclusive full-backend deployment choice for a smaller/simpler deployment". No
work item in this plan applies to it, and §6.2 adds a startup guard so the combination cannot be
configured by accident.

## 2. Findings

Findings §2.1–§2.5 are the four stores already catalogued in
`docs/operations/production-deployment.md`'s "In-process state that breaks under a second web
instance" table, plus one that table omits (§2.1, the key ring) and one that is new (§2.6, health
endpoints). Each is ordered by severity: §2.1 breaks authentication outright; the rest degrade
silently.

### 2.1 Data-protection key ring is per-instance (blocking)

`Program.cs` registers `AddDataProtection().PersistKeysToFileSystem(new(dataProtectionKeyPath))`
against a host directory; `JobTrack.Identity/ServiceCollectionExtensions.cs` adds
`SetApplicationName(DataProtectionApplicationName)` to the same registration. `SetApplicationName` is
already correct — it fixes the key-derivation purpose string so two instances *could* interoperate.
The storage location is not: each instance reads and writes its own filesystem directory and
generates its own key ring.

**Symptom:** the authentication cookie, the antiforgery token, and the ADR 0037 encrypted TOTP shared
secret are all protected by keys instance A holds and instance B does not. A user signing in on A is
anonymous on B; a form rendered by A fails antiforgery validation on B; a TOTP secret written by A
cannot be decrypted by B. **This is a hard failure, not a degradation, and sticky sessions do not fix
it** — an instance replaced or restarted still cannot read its predecessor's keys, and the TOTP
ciphertext is *durable* in the database, so a lost key ring is unrecoverable data, not just a
re-login.

Note this is a live latent risk even on the current single-server topology: losing that host
directory today already means unrecoverable TOTP secrets. That is an operational backup concern
(runbook §9.1), not a code defect, but it is the same key material.

**Target design:** persist the key ring in the shared PostgreSQL database via
`Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`'s `PersistKeysToDbContext<TContext>`,
backed by `PostgreSqlJobTrackIdentityDbContext` (which already owns the identity tables in the same
schema). Rationale over the alternatives:

- A shared network filesystem (NFS/EFS) would work but adds an availability dependency and a mount
  to the runbook that nothing else in the deployment needs.
- A cloud key vault contradicts ADR 0014's deliberate refusal to freeze a specific product in code.
- The database is already the single shared durable store, is already backed up to a defined RPO,
  and is already the thing whose loss is catastrophic — co-locating the key ring adds no new failure
  domain.

The `dataprotection_key` table is a new schema version script under
`database/postgresql/schema-versions/` (see §5). Keep `PersistKeysToFileSystem` as the configured
alternative for a single-instance deployment; make the choice explicit in configuration rather than
implicit, so a single-server install is not forced onto the database.

At rest the key ring is *unencrypted* XML unless a `ProtectKeysWith*` call is added. On the
single-server topology the filesystem ACL was the protection. In the database, the equivalent is
PostgreSQL's own access control plus the at-rest protection of the host — record this explicitly in
the runbook rather than leaving it implied, and treat `dataprotection_key` as credential-grade data
under spec §16 (never logged, never in a support export).

### 2.2 Session state is in-process despite the name

`Program.cs` calls `AddDistributedMemoryCache()` + `AddSession(...)`. `AddDistributedMemoryCache`
registers an `IDistributedCache` implementation that is *memory-backed and per-process* — the name
describes the interface, not the topology. `FilterMemory` (remembered per-page single-select filter
choices) is the only consumer.

**Symptom:** a user's remembered filter is visible only on the instance that set it and appears to
reset when the load balancer routes them elsewhere. Lowest severity of the five: this is
non-durable convenience state whose loss is already accepted on restart (the existing comment in
`Program.cs` says so).

**Target design:** two viable routes, and the choice is a genuine decision for the superseding ADR:

- **(a) Distributed cache.** Replace `AddDistributedMemoryCache()` with a shared `IDistributedCache`.
  `Microsoft.Extensions.Caching.StackExchangeRedis` is the obvious implementation but introduces a
  new infrastructure component — squarely the "distributed caching" ADR 0014 deferred.
  `Microsoft.Extensions.Caching.SqlServer` is not applicable; there is no first-party PostgreSQL
  `IDistributedCache`, so this route means either Redis or a hand-written EF-backed
  `IDistributedCache`.
- **(b) Drop server-side session entirely** and move filter memory into a data-protection-protected
  cookie. `FilterMemory` stores at most a handful of `long?` values keyed per page — well inside
  cookie size limits, and it is non-sensitive by nature (a worker/owner id the user just picked from
  a list they are already authorized to see). Once §2.1 gives every instance the same key ring, a
  protected cookie is automatically coherent across instances with **no new infrastructure at all**,
  and `AddSession`/`AddDistributedMemoryCache` are deleted outright rather than reconfigured.

**Recommendation: (b).** It removes a component instead of adding one, needs no Redis in the runbook,
and the state in question is exactly what a cookie is for. `FilterMemory`'s public shape
(`Remember`/`TryRecall`/`Resolve`) stays, with `ISession` swapped for an abstraction over the
request's cookies; its `Try*` expected-absence contract is unaffected. Confirm the cookie stays
`HttpOnly` / `Secure` / `SameSite=Lax` / `IsEssential`, matching the session cookie it replaces.

### 2.3 Login attempt rate limiting is per-instance

`LoginAttemptRateLimiter` holds its fixed windows in two `ConcurrentDictionary` instances in a
singleton.

**Symptom:** the configured limit multiplies by instance count — with three instances and
`LoginRateLimitPermitLimit = 20`, an attacker distributing attempts across the pool gets up to 60 per
window. The partitioned limiter and its backstop partition (`DefaultBackstopPermitMultiplier`) both
degrade identically.

**Mitigating factor, not a fix:** ASP.NET Core Identity's own lockout is database-backed
(`MaxFailedAccessAttempts = 5`, `LockoutMinutes = 15`), so per-account brute force is still bounded
correctly across instances. What degrades is the *rate* control layered above it — the defence
against credential-stuffing sweeps across many accounts and against the 2FA-code partition added by
the 2026-07-14 security audit remediation. Treat this as a security control weakening, not
disappearing.

**Target design:** move the window counters to a shared store. Given §2.2's recommendation avoids
Redis, prefer a PostgreSQL-backed fixed-window counter: one row per `(partition_key, window_start)`
with an `UPSERT … RETURNING` that increments and returns the post-increment count in one statement,
so the limiter needs no read-modify-write race handling. Per the house style this is EF-first; if
the atomic upsert cannot be expressed in LINQ, encapsulate it as a source-controlled stored function
invoked through EF (`HasDbFunction`/`ExecuteSql`), never an inline SQL string beside the call site.

Keep `LoginAttemptRateLimiter`'s current interface (`TryAcquire(partitionKey, backstopKey)`) and its
injected `TimeProvider`; introduce an abstraction behind it with the existing in-process
implementation retained as the single-instance option. **Do not delete the in-process
implementation** — it is the correct choice for the single-server topology and is already covered by
tests.

Expired-window pruning currently runs in-process on every `TryAcquire`
(`PruneExpiredPartitions`), bounded by `DefaultMaxPartitionCount = 4096`. The shared implementation
needs an equivalent bound and a pruning strategy that does not turn every login into a table sweep —
delete-on-write of rows older than one window, or a partial index plus a periodic cleanup invoked
from the same statement. Whichever is chosen, the `maxPartitionCount` cap must survive: it is the
defence against an attacker inflating the partition table itself.

### 2.4 External API rate limiting is per-instance

`Program.cs`'s `AddRateLimiter` policy uses `RateLimitPartition.GetFixedWindowLimiter` partitioned by
authenticated user name (falling back to remote IP). Partitions are per-process.

**Symptom:** identical multiplication to §2.3, for `/api/*` traffic — `ApiRateLimitPermitLimit = 120`
becomes an effective 360 across three instances.

**Target design:** the same shared counter as §2.3, exposed through a custom
`RateLimiterPolicy`/`PartitionedRateLimiter` that consults it. Note the framework's
`RateLimitPartition` factories are all in-process by design; a shared limiter means implementing
`RateLimiter` (or short-circuiting in the policy's `OnRejected` path against the shared counter)
rather than configuring an existing one. Reuse §2.3's store and its stored function — one shared
fixed-window primitive serving both call sites, not two.

Preserve the existing `OnRejected` behaviour exactly: 429 with `application/problem+json` and
`Type = RateLimitedProblemType`. The external API's published contract (ADR 0030) must not change
shape because the counter moved.

### 2.5 Pending PAT delivery is in-process

`PendingPatDeliveryStore` is a bounded (`DefaultCapacity = 64`), short-lived
(`DefaultDeliveryWindow = 2 minutes`), one-use, actor-scoped slot holding a freshly issued personal
access token's plaintext across the PRG hop introduced by the 2026-07-19 remediation:
`OnPostIssueAsync` reserves a slot, publishes the plaintext after the database commit, and redirects
to a GET carrying only an opaque `Guid` handle.

**Symptom:** if the redirect's GET lands on a different instance, the slot is not found and the user
never sees the token they just minted. The token exists and is live but its plaintext is
unrecoverable by design — so this is a **user-visible correctness bug producing an orphaned live
credential**, not a cosmetic one.

**Target design:** a shared store with the *same* semantics — bounded capacity, TTL, single
consumption, actor scoping, never logged. Options:

- **(a) Shared store (database table).** Preserves the current flow exactly. But it writes token
  plaintext to durable storage, which the current design deliberately avoids: today the plaintext
  exists only in process memory for at most two minutes. Persisting it — even briefly, even
  encrypted — enlarges the credential's exposure surface and puts it in every backup taken during
  the window. This needs explicit security sign-off, not an implementation decision.
- **(b) Carry the handle's payload in a data-protection-protected, single-use cookie** written on the
  POST response and consumed by the GET. Once §2.1 is done, any instance can decrypt it, and the
  plaintext never touches durable storage. Single-use enforcement becomes "the GET clears the
  cookie", which is weaker than the current server-side one-use guarantee (a client could replay the
  cookie before it is cleared) — but the payload is scoped to the actor who just created the token
  and is already in that actor's browser, so replay reveals nothing they do not already hold.
- **(c) Session affinity** at the load balancer, keeping the store as-is.

**Recommendation: (b)**, with (c) as the interim if the superseding ADR ships session affinity
anyway. Reject (a) unless the security review explicitly accepts persisting token plaintext. Whichever
is chosen, the `PendingPatDeliveryStore` doc comment's accepted-window caveat (an unpublished
reservation from a crash between commit and `Publish` simply expires) must be restated for the new
mechanism — it does not disappear, it changes shape.

### 2.6 No health or readiness endpoint

`Program.cs` maps no health endpoint. A load balancer needs one to decide whether an instance should
receive traffic, and a rolling deploy needs one to know when a new instance is ready.

**Target design:** `AddHealthChecks()` with two endpoints:

- `/health/live` — process is up. No dependency checks, so a database blip does not cause the
  orchestrator to kill otherwise-healthy instances.
- `/health/ready` — the instance can serve: a cheap `IJobTrackClient`-reachable probe and a
  data-protection key-ring read.

Both must be **anonymous** (add to `AnonymousPages`' equivalent for endpoints, since
`AuthorizeFolder("/")` is the default-deny backstop), **exempt from the `/api/*` rate limiter** (a
health poll must never consume a caller's budget or be throttled), and **leak nothing** — status code
plus a bare `Healthy`/`Unhealthy`, never exception detail, never a connection string, never a
dependency version. Restrict them to the reverse proxy's network at the proxy, not by an
application-level IP check.

The existing `WebHostSecurityArchitectureTests` must be extended so these two new anonymous endpoints
are asserted as a *closed* set — otherwise this change quietly widens the anonymous surface the
architecture test exists to police.

### 2.7 Rolling-deploy schema compatibility

Not a code defect, but a topology consequence that must be stated. On a single server, schema
deployment and application restart are one atomic-feeling operation. Under a rolling deploy, the old
and new application versions run concurrently against one database for the duration of the roll.

**Consequence:** every schema version script must be backward-compatible with the immediately
preceding application version for the length of a roll. A column rename or drop becomes a two-release
expand/contract sequence (add new → deploy app writing both → backfill → deploy app reading new →
drop old), not a single script.

ADR 0011's forward-only rule already points this way, and CLAUDE.md's pre-release exception ("edit
existing `NNNN_*.sql` in place") **must be revoked** before the first multi-instance deployment — it
is already scoped to "nothing has shipped", and a rolling deploy is shipping. Record this in the
superseding ADR, not only here.

## 3. Work items

Ordered by dependency. §3.1 is a hard prerequisite for §3.2 and §3.5 under their recommended
designs.

| # | Item | Finding | Depends on |
|---|---|---|---|
| 3.1 | Shared data-protection key ring (`PersistKeysToDbContext` + schema version script + config switch) | §2.1 | — |
| 3.2 | Replace server-side session with a protected cookie in `FilterMemory`; delete `AddSession`/`AddDistributedMemoryCache` | §2.2 | 3.1 |
| 3.3 | Shared fixed-window counter primitive (table + atomic upsert function + EF invocation) | §2.3, §2.4 | — |
| 3.4 | Route `LoginAttemptRateLimiter` and the `/api/*` limiter through 3.3, keeping in-process as the configured single-instance option | §2.3, §2.4 | 3.3 |
| 3.5 | Move pending PAT delivery to a protected single-use cookie | §2.5 | 3.1 |
| 3.6 | `/health/live` + `/health/ready`, anonymous, rate-limit-exempt, non-leaking | §2.6 | — |
| 3.7 | Startup guard: refuse to start when multi-instance mode is configured with a single-instance store or the SQLite provider | §1, all | 3.1–3.4 |
| 3.8 | Expand/contract schema discipline documented and CLAUDE.md's in-place-edit exception revoked | §2.7 | — |

**3.7 is the item that converts this whole plan from "documented footgun" to "enforced".** The
current failure mode is that a second instance starts happily and degrades silently. Introduce an
explicit `Deployment:Topology` configuration value (`SingleInstance` | `MultiInstance`); when
`MultiInstance`, fail startup closed — in the same style as the existing
`ForwardedHeaders`/`DataProtection` guards in `Program.cs` — if the key ring is filesystem-backed, if
any limiter is the in-process implementation, or if `Database:Provider` is `sqlite`. Anything less
reproduces exactly the silent-degradation problem the deployment doc already warns about.

## 4. What deliberately does not change

State these as non-goals so a future reader does not "fix" them:

- **PostgreSQL advisory locks (ADR 0012)** are already cross-instance correct. Do not replace them
  with anything.
- **The `version`-column optimistic concurrency** on every aggregate is already cross-instance
  correct. Do not add row-version columns or `xmin` mapping.
- **`SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero`** stays. It is what makes
  revocation coherent across instances; raising it to reduce database load would reintroduce a
  per-instance staleness window and contradict spec §7.1.
- **`IJobTrackClient` and `NpgsqlDataSource` as singletons** stay. Both are per-instance by design;
  connection pooling per instance is correct. Watch total pool size across instances against
  PostgreSQL's `max_connections` (a runbook number, §6.3), not a code change.
- **The in-process limiter implementations and `PersistKeysToFileSystem`** stay as configured options
  for the single-server topology. This plan adds a second supported topology; it does not delete the
  first.
- **No caching layer is introduced.** Every read still goes to PostgreSQL. Adding a cache is a
  separate, measured decision, and would bring its own cross-instance invalidation problem.

## 5. Schema changes

Two new tables, both under `database/postgresql/schema-versions/` as a new version script (per §2.7
this is a real forward-only script, not an in-place edit), with the matching
`database/sqlite/schema-versions/` script **only if** the SQLite provider is to remain schema-parallel
— which, given §1's provider constraint, it need not be for the limiter table. Decide explicitly
rather than defaulting; a deliberate provider divergence needs a comment in the script saying so.

- `dataprotection_key` — the shape `PersistKeysToDbContext` requires (`DataProtectionKeys`: `id`,
  `friendly_name`, `xml`). Follow the existing naming/type conventions; do not accept EF's default
  pluralized PascalCase names.
- The fixed-window counter table for §3.3 — `(partition_key, window_start, permits_used)` with a
  primary key on the first two and an index supporting expiry pruning. Column types per the
  2026-07-11 column-type remediation conventions.

Both are operational, not domain, tables: they carry no `IJobTrackClient` surface, no entity in
`docs/database-entities.md`, and no public API. Keep them out of the domain model deliberately.

## 6. Operational changes

### 6.1 Runbook

`docs/operations/production-deployment.md` currently states multi-node deployment is out of scope and
carries the four-row in-process-state table at §246. On implementation:

- Add the multi-instance topology as a documented, supported option alongside the single-server one —
  do not replace the single-server runbook, which remains the recommended default.
- Replace the four-row table with a table stating, per store, which topology each configured
  implementation is valid for. **Add the fifth row (§2.1, the key ring) the current table omits** —
  and add it now, ahead of any implementation, because it is the one whose absence is most dangerous
  (an operator reading that table today would conclude the four listed items are the whole list).
- Document load-balancer requirements: TLS terminates at the proxy (unchanged from ADR 0014),
  `ForwardedHeaders:KnownProxies`/`KnownNetworks` must list *every* proxy in the pool (the existing
  fail-closed guard already enforces non-empty, but not completeness), health-check paths and
  expected codes, and drain-on-shutdown behaviour.
- Document the deploy sequence: schema deploy (advisory-locked, already safe) → roll instances one at
  a time → verify each `/health/ready` before proceeding.

### 6.2 Configuration

New keys, each with the fail-closed treatment the existing security-relevant keys get:

- `Deployment:Topology` — `SingleInstance` (default, preserving current behaviour) | `MultiInstance`.
- `DataProtection:Store` — `FileSystem` (default) | `Database`. `DataProtection:KeyPath` stays
  required when `FileSystem`.
- `RateLimiting:Store` — `InProcess` (default) | `Database`.

Defaults must preserve today's behaviour exactly, so an existing single-server deployment upgrades
with no configuration change and no behavioural difference.

### 6.3 Capacity

Record in the runbook, not in code: total PostgreSQL connections is now
`instances × per-instance pool size`, and must stay within `max_connections` with headroom for
`JobTrack.Database` deploys and operator sessions. This is the one capacity number multi-instance
actually changes.

## 7. Superseding ADR

Draft `docs/decisions/00NN-multi-instance-web-deployment.md` **before** any implementation. It must:

- State that it supersedes ADR 0014's deployment-topology decision **only** — 0014's secret-source
  and RPO/RTO decisions are unaffected and stay in force.
- Record the measured capacity or availability requirement that justifies revisiting it. ADR 0014 is
  explicit that revisiting is "warranted only by a measured requirement, per plan §9.1 and §9.3". A
  plan is not a measurement; **this ADR cannot be written until that number exists.**
- Close the two genuine open decisions this plan surfaces rather than deciding them unilaterally:
  §2.2's cookie-vs-distributed-cache choice, and §2.5's "may PAT plaintext ever be persisted?"
  question.
- Adopt the expand/contract schema discipline from §2.7 and revoke CLAUDE.md's in-place-edit
  exception.
- State that the single-server topology remains supported and is still the default.

## 8. TDD approach

Per CLAUDE.md, failing test first for every item. The specific difficulty here is that the defects
are *cross-instance*, and the existing web test suite is single-`WebApplicationFactory`.

- **Two-host integration fixture.** The load-bearing new test infrastructure: stand up two
  `WebApplicationFactory` instances sharing one PostgreSQL fixture database, and assert cross-instance
  coherence directly — sign in on host A, make an authenticated request on host B; render a form on
  A, post it to B; consume a rate-limit budget on A, observe it exhausted on B; issue a PAT on A,
  fetch the plaintext from B. **Write these first, against the current code, and watch each one fail
  for exactly the reason §2.1–§2.5 predict.** That failure set is the plan's own evidence that the
  findings are real, and it belongs in the commit trail.
- **Key ring (§3.1):** contract test that two hosts with distinct filesystem paths but the shared
  database store decrypt each other's payloads, and the negative case that filesystem-backed hosts do
  not.
- **Shared counter (§3.3):** provider-specific concurrency test proving the atomic upsert does not
  lose increments under concurrent connections — the per-slice concurrency test the house style
  requires for any compound write.
- **Startup guards (§3.7):** one test per invalid combination asserting the specific
  `InvalidOperationException`, matching the existing `ForwardedHeaders`/`DataProtection` guard tests.
- **Health endpoints (§3.6):** anonymous access, rate-limiter exemption, and a non-leaking body on the
  unhealthy path; plus the `WebHostSecurityArchitectureTests` extension asserting the anonymous
  endpoint set stays closed.
- **Existing suites must stay green unchanged** under default (`SingleInstance`) configuration. That
  is the proof that the second topology was added rather than the first replaced.

## 9. Estimate and sequencing note

§3.1 alone (plus §3.6 and the two-host fixture) is the minimum that makes multi-instance *function*.
§3.2–§3.5 are what make it *correct*. A deployment that does §3.1 and adds session affinity at the
load balancer would work in practice, with §2.3/§2.4's rate-limit multiplication as the accepted
residual risk — but session affinity is a weak guarantee (it breaks on instance replacement, which is
exactly what a rolling deploy does), so treat it as an interim, and only with the residual risk
written into the ADR rather than discovered later.

Do not ship §3.1 alone without §3.7's guard. Making multi-instance *possible* while leaving the other
four stores silently per-instance is strictly worse than today, because it removes the failure that
currently stops an operator from trying.
