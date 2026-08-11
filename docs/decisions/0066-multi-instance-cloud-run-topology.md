# ADR 0066: Cloud Run may scale to multiple instances against one Cloud SQL database

**Status:** Accepted
**Amends:** ADR 0062. The single-instance constraint (`--max-instances=1`) that ADR 0062 recorded as
a correctness requirement, not a cost setting, is superseded by this ADR once
`docs/plans/2026-07-26-multi-instance-web-deployment-plan.md` ships. ADR 0062's remaining decisions
(Cloud Run + Cloud SQL as a supported topology, Secret Manager, backup/PITR posture,
`ForwardedHeaders__KnownNetworks__0=0.0.0.0/0`) are unchanged.

## Context

ADR 0062 pinned `--max-instances=1` as a correctness constraint because four in-process stores
(login rate limiter, external API rate limiter, session-backed filter memory, pending PAT delivery)
and the filesystem/GCS-mounted data-protection key repository assume a single process. The
multi-instance plan replaces each of those with a PostgreSQL-backed or protected-cookie equivalent.

**Motivation.** This is not a response to a measured capacity shortfall — current production load is
low and a single instance currently serves it without incident. The motivation is architectural: (1)
demonstrate the multi-instance technique end-to-end while the system is small enough to do so safely,
and (2) remove the single-instance ceiling before growth makes it load-bearing. There is accordingly
no SLO or incident driving a specific throughput target; the acceptance bar is functional (every
cross-host correctness scenario in the plan's evidence matrix passes) rather than a capacity number.

**Inventory confirmation (Stage 0 item 2).** A full search of `JobTrack.Web` and `JobTrack.Identity`
confirms the plan's §2 list of in-process state is complete:

- data-protection keys — `PersistKeysToFileSystem` (`src/JobTrack.Web/Program.cs:488`)
- `FilterMemory` — `AddDistributedMemoryCache()`/`AddSession()`/`UseSession()`
  (`src/JobTrack.Web/Program.cs:198-199,628`), consumed by `src/JobTrack.Web/FilterMemory.cs`
- login rate limiter — `LoginAttemptRateLimiter`, an in-memory `ConcurrentDictionary`-backed bounded
  window (`src/JobTrack.Web/Program.cs:426`, `src/JobTrack.Web/LoginAttemptRateLimiter.cs`)
- external API rate limiter — ASP.NET Core's in-process `AddRateLimiter` policy
  (`src/JobTrack.Web/JobTrackApi.cs:41,97`)
- `PendingPatDeliveryStore` — in-process `Dictionary<Guid, Entry>` under a `Lock`
  (`src/JobTrack.Web/Program.cs:192`, `src/JobTrack.Web/PendingPatDeliveryStore.cs`)

No hosted service, timer, or other filesystem write exists in either project. One additional static
mutable collection was found — `EnumDisplay.Labels`, a `ConcurrentDictionary<Enum, string>` memoizing
enum display strings (`src/JobTrack.Web/EnumDisplay.cs:18-20`). Its cardinality is bounded by the
fixed set of domain enums, it never becomes cross-instance-inconsistent (any host derives the same
label for the same enum value), and it requires no remediation.

## Decision

1. Cloud Run may run more than one container instance against one Cloud SQL database once the plan's
   Stages 1–7 pass their exit criteria.
2. Correctness never relies on session affinity; Cloud Run session affinity is not enabled as a
   substitute for the plan's stateless-host design.
3. PostgreSQL is the shared store for data-protection keys (Stage 2) and rate-limit counters
   (Stage 5). Filter memory (Stage 3) and pending PAT delivery (Stage 4) use protected, principal-
   bound client cookies instead of a shared store — no session/cache service is added for them.
   **Documented residual for pending PAT delivery:** unlike the old in-process store's true
   single-consumption dictionary entry, the `JobTrack.PendingPat` cookie's deletion after display is
   a client instruction (Set-Cookie), not server-enforced one-time use — a client that captures and
   replays the raw cookie value within its short window can decrypt it again. That replay can only
   ever reveal a token already delivered once to that same authenticated actor (the protector
   purpose is bound to Identity's own row id) and self-expires quickly; it is accepted rather than
   engineered away, and recorded in
   `docs/threat-model/web-authentication-threat-model.md` row 13.
4. ADR 0014's single-server topology and ADR 0062's single-instance Cloud Run topology remain
   supported and unchanged; this ADR adds a multi-instance variant of the Cloud Run topology, it does
   not retire the single-instance one.
5. SQLite is unsupported in multi-instance mode. `Deployment:Topology=MultiInstance` with
   `Database:Provider=Sqlite` fails startup (Stage 6).
6. The GCS-to-PostgreSQL data-protection key-ring migration (Stage 2) is a credential-data migration:
   every existing key must remain readable afterward. It is never treated as a disposable rotation.

   **Exception taken for this deployment (2026-08-09, operator decision).** The standing rule above
   is unchanged for any deployment holding a key ring worth preserving. It was waived once, here,
   with explicit operator consent, on the finding that:
   - `project-e2ce9938-0f7b-48a8-b0d` did hold a live ring in
     `gs://…-jobtrack-dpkeys` (one key). The earlier claim in the Stage 2 status note that this
     deployment "carries no production key ring to import" was **wrong**, and is corrected there.
   - That ring was stored **unencrypted** — its XML carries `<masterKey><value>` and the framework's
     own "unencrypted form" warning, with no `EncryptedData`/`encryptedKey`/`X509Certificate`
     element, despite the deployment provisioning a certificate for encryption at rest. Read access
     to the bucket was therefore equivalent to holding every session cookie and TOTP secret.
   - Everything of lasting value is in PostgreSQL and unaffected. Discarding the ring signs every
     user out (recoverable by signing in) and makes enrolled TOTP secrets undecryptable (recoverable
     by `reset-2fa` and re-enrolment). Personal access tokens are hash-verified, not data-protected,
     so they keep working.

   Moving to the PostgreSQL store **improves** encryption at rest rather than merely relocating it:
   `Program.cs` applies `ProtectKeysWithCertificate`, so keys generated after the cutover are
   encrypted. Consequently no import command was built (Stage 2 items 4–5), because under this
   decision it has no consumer.
7. Every PostgreSQL schema change from this plan onward uses expand/contract delivery (plan §2.8).
   The pre-release permission to edit numbered PostgreSQL schema scripts in place (this file's sibling
   convention in `CLAUDE.md`) ends once the first multi-instance production migration is released;
   SQLite's pre-release policy is unaffected.
8. The shared rate limiter fails closed: a counter-store failure returns the existing non-disclosing
   login failure or a 503 Problem Details response for the API. It never falls back to an in-process
   counter under `MultiInstance` topology.
9. **Acceptance objective:** functional, not a numeric SLO. The rollout is accepted when every
   scenario in the plan's §5 evidence matrix passes on the two-host fixture (Stage 1), the OrbStack
   topology (Stage 7), and Cloud Run itself (Stage 8) — not when a throughput target is met, because
   no such target currently exists. Revisit this decision with a real target if and when measured
   load approaches the single-instance ceiling.
10. **Cloud Run instance/concurrency policy**, chosen for the demonstration goal rather than a
    measured peak (re-verify before raising further — see plan §8):
    - Service-level `--min=0` (unchanged in effect from ADR 0062; cold start is acceptable at current
      load).
    - Service-level `--max=2`, the smallest value that can prove multi-instance correctness in
      production (Stage 8 item 7's "verify the Cloud Run instance metric shows the expected
      simultaneous hosts" needs at least two).
    - Container concurrency is set explicitly to 80. It retains the measured default but cannot drift
      through an inherited revision setting; concurrency is a request-routing control independent of
      instance count and is not currently a constraint at this scale.
11. **Connection budget (plan §2.7 formula, current tier).** `db-f1-micro`'s documented
    `max_connections` ceiling for its shared-core class is small (on the order of 25; re-verify the
    exact current value against Cloud SQL documentation before deploy, per plan §8). Reserving
    headroom for operator/admin connections and applying
    `planned_peak_hosts = service_max(2) + 1 overshoot host + 1 tagged candidate = 4` leaves too
    little per-host capacity to give the four distinct connection-string pools
    (domain, Identity, PAT management, PAT authentication) a workable `Maximum Pool Size` on this
    tier. **This is expected, not a blocker for Stage 0–7 code work** (those stages run against the
    existing local/CI PostgreSQL, not `db-f1-micro`): Stage 8 must either raise the Cloud SQL tier or
    reduce `planned_peak_hosts`/pool counts before enabling multi-instance in production, and must
    record whichever it chooses with the measured numbers, not the estimate here. The service-level
    maximum is divided across traffic-serving revisions, so rolling overlap stays inside that cap;
    the tagged no-traffic candidate is started outside its allocation.

    **Resolved (Stage 8):** upgraded to `db-custom-1-3840` (1 vCPU, 3.75 GiB, dedicated core), whose
    documented `max_connections` for that memory bracket is 100 — chosen over shrinking
    `planned_peak_hosts`/pool counts on `db-f1-micro`, which would have left as little as 1 connection
    per pool per host. With `operator_and_deployment_reserve = 10` and
    `planned_peak_hosts = 4`, `host_budget = floor((100 − 10) / 4) = 22`. Per-host `Maximum Pool Size`
    is split by measured traffic shape rather than evenly: domain 10, Identity 6, PAT management 3,
    PAT authentication 3 (total 22, exactly the budget). `scripts/deploy-cloudrun-postgresql.sh`
    computes and validates this arithmetic from named values at deploy time and refuses to proceed if
    the configured pool sizes exceed the calculated budget. This tier costs more than the free-tier
    `db-f1-micro`; re-verify the 100-connection figure against current Cloud SQL documentation before
    every production deploy, since Cloud SQL may revise the memory-to-`max_connections` table.

    **Confirmed against the live instance (2026-08-09).** The provisioning job now asks the server
    directly and refuses to provision below the assumed ceiling; it reported `100 available, at least
    100 required`, so the documented figure and the running instance agree. The figure is no longer
    taken on documentation alone.

12. **Deployed and accepted (2026-08-09).** Decision 9's acceptance bar is met for the
    two-instance topology: revision `jobtrack-web-pg-00076-yok` served from two simultaneous
    instances under load with zero failures across 10,000 requests, no session affinity, against one
    Cloud SQL database. The GCS key ring was retired as decision #6's exception describes — volume
    and mount detached, IAM binding removed, bucket deleted with 30-day soft-delete remaining.

    One scenario resisted production measurement and is recorded rather than glossed: a *forced*
    cross-host antiforgery round trip. Cloud Run exposes no per-instance addressing, so 24 tagged
    probe pairs under concurrent load all landed on a single instance. Cross-host correctness rests
    on Stage 1's two-host fixture and Stage 7's OrbStack topology, both deterministic, plus the
    structural argument that two instances sharing one PostgreSQL key ring cannot hold different
    keys. Raising `max-instances` past 2 (Stage 8 item 8) has not been done.

## Consequences

- Stages 1–7 can proceed against this decision without a numeric capacity target blocking them.
- Stage 8 (Cloud Run deploy) is blocked until the `db-f1-micro` connection budget is resolved by
  measurement (upgrade tier, or accept a smaller `max-instances`/pool combination and record why).
- A future ADR is needed only if a real throughput/SLO target emerges; this ADR's acceptance bar
  stays functional in the meantime.
- `docs/operations/postgresql-cloud-run-deployment.md` and `docs/operations/production-deployment.md`
  are updated in Stage 9 to reflect the new topology, matrix, and rollback procedure.
