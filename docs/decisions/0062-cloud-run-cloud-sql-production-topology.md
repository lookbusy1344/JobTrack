# ADR 0062: Cloud Run + Cloud SQL is a supported production topology

**Status:** Accepted
**Amends:** ADR 0014. The single-server topology it describes remains supported and unchanged; this
ADR adds a second, managed topology rather than replacing it. ADR 0014's secret-source and recovery
policies are reaffirmed here with their managed-service equivalents.
**Amended by:** ADR 0066, which supersedes this ADR's `--max-instances=1` single-instance
correctness constraint once the multi-instance plan's Stages 1–7 land. Every other decision here is
unchanged.

## Context

ADR 0014 fixed the initial-release topology as one modest server, no containers, with the explicit
note that revisiting it required a measured requirement. The PostgreSQL Cloud Run deployment
(`scripts/deploy-cloudrun-postgresql.sh`, documented in
`docs/operations/postgresql-cloud-run-deployment.md`) has since matured past its demo origins into
the deployment the owner intends to run in production, and its gap list closed with the 2026-08-01
security audit remediation and the 2026-08-06 Cloud Run isolation plan. What remained was the
mismatch this ADR resolves: the operations doc itself noted "ADR 0014 still says single-server, no
containers. Nothing here amends it."

The mismatch is not cosmetic. Two production-correctness settings are valid **only** under this
topology, so the topology must be pinned by decision, not convention:

- **`ForwardedHeaders__KnownNetworks__0=0.0.0.0/0`** is safe on Cloud Run because the container is
  unreachable except through Google's front end, which always sets the forwarded headers. On any
  other topology the same value is a spoofable client address and scheme, which is why
  `Program.cs` otherwise fails startup closed when no trusted proxy is configured.
- **`--max-instances=1`** is a correctness constraint, not a cost setting: four in-process stores
  (login rate limiter, API rate limiter, session-backed filter memory, pending PAT delivery) assume
  a single process. Horizontal scaling is gated on
  `docs/plans/2026-07-26-multi-instance-web-deployment-plan.md`, which remains blocked on a
  superseding ADR of its own.

## Decision

Cloud Run + Cloud SQL, deployed exclusively by `scripts/deploy-cloudrun-postgresql.sh`, is a
supported production topology for JobTrack alongside ADR 0014's single server:

- **Application:** one Cloud Run service, `--min-instances=0 --max-instances=1`, image attestation
  enforced by the deploy script's `--binary-authorization=default` flag. The script is the only
  sanctioned deploy path — a live negative test proved no GCP org-policy control can make
  attestation mandatory project-wide
  (`docs/plans/2026-08-06-cloudrun-persistent-isolation-plan.md` §2.2), so an out-of-band deploy
  would bypass attestation silently. That residual is risk-accepted in ADR 0063.
- **Database:** Cloud SQL for PostgreSQL with automated backups, point-in-time recovery, retained
  final backups, deletion protection, `ssl-mode=ENCRYPTED_ONLY`, and connector enforcement — the
  managed realization of ADR 0014's staged backup/recovery policy.
- **Secrets:** Secret Manager holds role passwords and the data-protection certificate material;
  the running service can read only what it is granted — ADR 0014's external-secret-store policy,
  unchanged in substance.
- **Data-protection keys:** persisted on a GCS-backed volume mount and encrypted at rest with a
  certificate whose private material lives only in Secret Manager, satisfying ADR 0014's "protected,
  durable directory" requirement on an ephemeral-filesystem platform.

The two topologies remain mutually exclusive per deployment. SQLite remains a third, mutually
exclusive full-backend choice for embedded/demo use (ADR 0014, unchanged), and the SQLite Cloud Run
demo (`scripts/deploy-cloudrun.sh`) remains a throwaway demo, not a production path.

## Consequences

- The topology-bound settings above are now correct by decision: a future move off Cloud Run must
  revisit this ADR before reusing its configuration, in particular the blanket forwarded-headers
  trust.
- Multi-instance operation stays out of scope until the multi-instance plan's own superseding ADR
  lands; nothing here weakens that gate.
- `docs/operations/postgresql-cloud-run-deployment.md` is the operational runbook for this
  topology; `docs/operations/production-deployment.md` remains the runbook for ADR 0014's
  single-server topology.
