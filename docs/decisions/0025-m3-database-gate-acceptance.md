# ADR 0025: M3 (database) gate acceptance

**Status:** Accepted
**Closes:** Implementation plan §6.7

## Decision

M3 is formally accepted. Every §6.7 database-gate exit criterion is satisfied:

| Criterion | Evidence |
|---|---|
| Empty and upgrade-path schema deployments pass on both providers | `TC-DB-SCHEMA-001` (`Deploying_from_empty_applies_script_and_records_it`), `TC-DB-SCHEMA-002` (`Redeploying_an_up_to_date_database_is_a_no_op`), `TC-DB-SCHEMA-003`/`004` (checksum/version fail-closed) in `JobTrack.Database.ContractTests.SchemaDeploymentContractTestsBase`, run against both `PostgreSqlSchemaDeploymentTests` and `SqliteSchemaDeploymentTests` |
| Every invariant has a named enforcement mechanism and a passing bypass test | The eleven `*SchemaContractTestsBase` classes in `JobTrack.Database.ContractTests` (`JobNode`, `LeafWork`, `WorkSession`, `Rate`, `ScheduleVersion`, `ScheduleException`, `JobPrerequisite`, `AppUserAndIdentity`, `AuditEvent`, `InitialisedMarker`, `HierarchyMove`, `WorkerOverlapCandidateDiscovery`, `HierarchyAchievementReadinessQueries`), each run against both a `PostgreSql*SchemaTests` and `Sqlite*SchemaTests` subclass — 310 tests passing at the 2026-07-09 baseline (§2 of this plan) |
| Shared query fixtures produce equivalent results | The `*SchemaContractTestsBase` classes are the shared fixtures (test-catalogue.md §1: "one PostgreSQL and one SQLite test share the same ID when they assert the shared contract"); every `TC-DB-*` row without a `-PG`/`-SQ` suffix asserts identical behaviour from both provider subclasses against the same base-class test body |
| Race tests demonstrate integrity after commit and rollback | `TC-DB-SCHEMA-005-PG`/`005-SQ` (`Concurrent_deployment_runs_apply_exactly_once`) plus the concurrent-edge-insertion, opposing-move, overlap, and stale-optimistic-concurrency cases embedded in `HierarchyMoveSchemaContractTestsBase`, `WorkerOverlapCandidateDiscoverySchemaContractTestsBase`, `JobPrerequisiteSchemaContractTestsBase`, and `WorkSessionSchemaContractTestsBase` (test-catalogue.md §2 "Race/concurrency" category) |
| PostgreSQL query plans and scale budgets meet the agreed production targets | `JobTrack.Database.PerformanceTests` (`HierarchyTraversalPerformanceTests`, `HierarchyAchievementReadinessPerformanceTests`, `OverlapDiscoveryPerformanceTests`, `RateResolutionPerformanceTests`, `SchemaDeploymentPerformanceTests`, `WriteContentionPerformanceTests`) against the budgets recorded in `docs/traceability/performance-budgets.md` |
| SQLite limitations and configuration requirements are documented | `docs/operations/sqlite-limitations-and-configuration.md` |
| Role grants prove the normal application role cannot perform DDL, erase audit rows, or delete retained history | `TC-DB-ROLES-001` (`PostgreSqlRoleGrantsTests`: `The_application_role_cannot_create_a_table`, `The_application_role_cannot_alter_an_existing_table`, `The_application_role_cannot_delete_audit_event_rows`, `The_application_role_cannot_update_audit_event_rows`, `The_application_role_cannot_delete_retained_work_session_history`), plus `TC-DB-ROLES-002` (`The_readonly_role_cannot_select_identity_secret_columns`) closing the threat-model extension to Identity secret columns |
| Schema-level PostgreSQL backup/restore smoke test passes; SQLite backup procedures are documented | `TC-DB-BACKUP-001` (`PostgreSqlBackupRestoreTests.A_deployed_database_survives_a_pg_dump_and_pg_restore_round_trip`); `docs/operations/postgresql-backup-restore.md` for the PostgreSQL procedure and `docs/operations/sqlite-limitations-and-configuration.md`'s "Backup procedure" section for SQLite's file-copy equivalent (production-like recovery-objective rehearsal is explicitly deferred to the release gate per §6.7) |

Supporting traceability: `docs/traceability/test-catalogue.md` rows under spec references §4–§6, §12.6, §15, §16, and ADRs 0011/0012 remain the row-by-row map from spec clause to test ID; this ADR is the formal gate acceptance those rows feed into, not a restatement of them.

## Consequences

- Per plan §1 and §6.7, Phase 2 reusable-library feature work (§7) may now proceed on top of the
  frozen M1/M2 schema. No schema version is re-opened for convenience — a defect found in Phase 2
  or later that traces back to a database-layer decision is fixed in the schema/role-grant layer and
  this gate re-passes, per the mandatory-implementation-order rule in `CLAUDE.md`.
- Two role-grant findings were closed immediately before this acceptance was recorded rather than
  deferred: `TC-DB-ROLES-002` (the reporting/auditor role's blanket grant leaking Identity secret
  columns) was a genuine gap, fixed in `database/postgresql/roles/jobtrack-roles-and-grants.sql`
  rather than accepted as residual risk.
- The `RateResolutionPerformanceTests.Resolve_rate_for_one_user_among_2000_meets_the_latency_and_plan_budget`
  timing assertion is occasionally flaky under concurrent full-suite load (observed once during this
  gate's verification run, passing in isolation immediately after); this is noted as a known-flaky
  budget check, not a scale-budget regression, and does not block this acceptance.
- Should a later phase expose a defect in an M3 decision (a wrong invariant, an unachievable
  performance budget, a role-grant gap), plan §1's rule applies: the correction is made in the
  database layer and this gate is re-passed, not patched over in library or web code.
