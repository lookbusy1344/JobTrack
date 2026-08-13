# ADR 0063: 1.0 release-gate acceptance and documented risk acceptances

**Status:** Accepted (owner sign-off, 2026-08-06)
**Closes:** Implementation plan §9.4 (release gate). Completes the gate sequence recorded in ADR
0025 (M3, database), ADR 0026 (M6, library), and ADR 0027 (M8, web).

## Context

Plan §9.4 permits an initial release for a single, employee-only internal deployment once the
mandatory criteria are met, with named heavier exercises deferrable "behind a documented,
signed-off risk acceptance". This ADR is that document: it records the evidence for each mandatory
criterion and enumerates every deliberate deferral so that none of them has to be rediscovered.

## Evidence for the mandatory criteria

| §9.4 criterion | Evidence |
|---|---|
| Authoritative acceptance criteria have linked passing evidence | ADRs 0025/0026/0027 and the traceability catalogue (`docs/traceability/`) |
| Both provider conformance suites pass; PostgreSQL production-scale tests pass | Full solution suite green at the release commit (2026-08-06); performance lane (`scripts/perf-test.sh`) enforces the 200,000-node PostgreSQL budgets in `docs/traceability/performance-budgets.md` as regression ceilings |
| Threat-model mitigations verified | `docs/threat-model/web-authentication-threat-model.md` rows all carry named test IDs; the 2026-07-11, 2026-07-14, and 2026-08-01 security audit plans are Remediated/Implemented |
| Dependency/secret/static-analysis scans pass | `-warnaserror` build (analyzers + architecture tests) clean; transitive NuGet vulnerability scan clean (2026-08-01); no secrets in the repository (CLI secret channels reject argv, secret material lives in Secret Manager) |
| Backup passed a restore smoke rehearsal | `TC-DB-BACKUP-001` (`pg_dump`/`pg_restore` round trip incl. grant verification) passes in the suite; Cloud SQL automated backups + PITR are provisioned by the deploy script |
| Upgrade from every supported schema version rehearsed | Trivially satisfied at 1.0: this release *is* the first deployment, and ADR 0011's forward-only rule becomes binding from it (see Consequences) |
| Public API and HTTP compatibility reports reviewed | FDG compliance plan (2026-07-26) implemented; external HTTP API contract per ADR 0029/0030 with its remediation plan implemented |
| Runbooks and ownership exist | `docs/operations/postgresql-cloud-run-deployment.md` (topology per ADR 0062): provisioning, schema upgrade (expand/contract), password/role rotation, emergency reset, teardown |
| No known high-severity security or data-integrity defect | None open; residuals below are documented and accepted |

## Risk acceptances

Accepted by the owner for the 1.0 internal release. Each names its revisit trigger.

1. **Observability (plan §9.2) is deferred post-1.0.** No OpenTelemetry traces/metrics/logs, no
   alerting or dashboards beyond what Cloud Run provides by default. Acceptable for a single
   low-traffic internal instance whose logs are centrally captured by Cloud Logging. Revisit:
   first production incident that Cloud Logging alone cannot diagnose, or any growth in user base.
2. **External penetration testing is deferred** (§9.4 names it as deferrable). Three internal
   security audits with full remediation stand in. Revisit: before any exposure beyond the
   employee-only audience.
3. **PostgreSQL credential blast-radius residuals (security audit 2026-08-01 §2.6,
   threat-model row 13).** `jobtrack_domain` shares `identity_user` secret-column access with
   `jobtrack_identity` (credential-transition commands update those columns in the same transaction
   as their audit row), and retains direct append-only `audit_event` insertion. Compromise of the
   ordinary domain credential can therefore read password hashes and append plausible audit rows.

   **Remediated 2026-08-13:** `jobtrack_credential_administration` now owns those command ports and
   their authentication audit path. `jobtrack_domain` has column-level access only to non-secret
   account facts, cannot mutate Identity or role rows, and a database trigger refuses the fixed
   credential/role/authentication audit operation names from that role. The remaining residual is
   limited to misattribution of domain audit events for mutation capabilities the role genuinely has.
   Mitigations already in place: capability-specific runtime roles, PAT secrets behind `SECURITY DEFINER`
   functions only, append-only audit enforcement. Revisit: any multi-tenant or externally exposed
   deployment.
4. **Binary Authorization is enforced by the deploy script's own flag, not a platform control.**
   Proven unenforceable project-wide by live negative test
   (`docs/plans/2026-08-06-cloudrun-persistent-isolation-plan.md` §2.2). Accepted on the basis that
   `scripts/deploy-cloudrun-postgresql.sh` is the only sanctioned deploy path (ADR 0062). Revisit:
   if GCP ships a Cloud Run org-policy constraint that can make attestation mandatory.
5. **Two environment-level tests remain deferred** (2026-08-01 audit): the live TLS test against a
   real trusted CA (validator-level unit tests prove fail-closed configuration) and OS-level
   child-process argv/stdout inspection (testable-abstraction unit tests stand in). Evidence gaps,
   not known defects.
6. **A production-like RPO/RTO restore rehearsal has not been run** (§6.7 assigned it to this
   gate). The automated schema-level smoke test passes and Cloud SQL provides automated backups,
   PITR, and retained final backups. Accepted with a commitment to rehearse a full restore against
   a production-like copy within the first operational quarter, before the database accumulates
   irreplaceable history.

## Consequences

- **The first production deployment makes ADR 0011's forward-only schema rule binding.** From the
  release commit onward, `database/*/schema-versions/` scripts are never edited in place; every
  change is a new numbered script compatible with the revision still serving traffic
  (expand/contract, per the deployment runbook). The release commit should be tagged to make the
  boundary unambiguous.
- Post-1.0 backlog, in no committed order: observability baseline (§9.2), a database-touching
  health endpoint, HSTS max-age increase, the PostgreSQL column-type optimizations
  (`docs/plans/2026-07-11-postgresql-column-type-remediation-plan.md`, deferred), multi-instance
  support (blocked on its own ADR), and the restore rehearsal in item 6. Ordered and gated in
  `docs/plans/2026-08-06-post-1.0-improvement-plan.md`, which also carries the trigger-based
  register for the risk acceptances above.
- Public-surface compatibility discipline (impl plan §7.5, CLAUDE.md "Public API discipline") now
  applies with a shipped consumer in existence, not merely as policy.
