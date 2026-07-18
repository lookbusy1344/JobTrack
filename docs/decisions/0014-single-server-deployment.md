# ADR 0014: Single-server deployment, secret source, and PostgreSQL recovery objectives

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 11

## Decision

**Deployment topology.** The initial release targets one modest server for an employee-only internal system (plan §9.1, §9.4): the ASP.NET Core application runs as a dedicated unprivileged OS service behind a locally managed reverse proxy; PostgreSQL runs on the same server or a directly managed database host; SQLite is a mutually exclusive full-backend deployment choice for a smaller/simpler deployment, not a PostgreSQL fallback (plan §1, §6). No containers, no orchestration, no multi-node coordination, no distributed cache — these are deliberately deferred until a measured capacity or availability requirement justifies them (plan §9.1), not designed for speculatively now.

**Secret source.** Production secrets (database credentials, data-protection keys' backing material, any external service credentials) are held in an **external secret store** appropriate to the host environment (OS-level protected configuration, a secrets manager, or an environment-injected value at service start — the specific product is an operational choice made at deployment time, not frozen in code). Secrets are never placed in deployment scripts, configuration files committed to source control, or logs (plan §9.1). Data-protection keys are persisted in a protected, durable host directory readable only by the application's service account (plan §8.2, §9.1).

**PostgreSQL recovery objectives.** For the initial release:

- **RPO (recovery point objective):** loss of no more than the most recent completed backup cycle's worth of committed transactions — the specific interval is set when the backup schedule is defined in the operations runbook (§9.1), and is a deployment-time operational parameter, not hardcoded into application behaviour.
- **RTO (recovery time objective):** restoring service on the single-server topology is a manual, runbook-driven restore from the most recent backup; there is no automatic failover in the initial release (consistent with "no multi-node coordination" above). The specific target duration is set in the same runbook.
- A schema-level backup/restore smoke test is a **database gate** requirement (§6.7); a production-like recovery-objective rehearsal against the actual RPO/RTO targets is a **release gate** requirement (§9.4) — the two are distinct maturity levels and are not conflated.

## Consequences

- The operations runbooks (§9.1) are the place these objectives are made concrete (backup interval, restore procedure, verified restore time) — this ADR fixes the *policy* (single server, external secrets, staged smoke-test-then-rehearsal), the runbook fixes the *numbers*.
- Kestrel binds to a private loopback endpoint or local socket; HTTPS terminates at the reverse proxy (plan §9.1) — the application itself never handles a public network interface directly.
- Revisiting this ADR (multi-node, managed database service, automated failover) is warranted only by a measured requirement, per plan §9.1 and §9.3 — not anticipated here.
