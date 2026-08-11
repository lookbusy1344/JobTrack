# ADR 0068: Deleting a job node takes its request-intake rows with it

**Status:** Accepted
**Amends:** ADR 0034 (`job_request_note` append-only) — append-only now stops at the request's own
lifetime. **Completes:** ADR 0061 (recursive subtree deletion) — its declared cascade over
`job_request_note` could never actually run. **Extends:** ADR 0036 (single-node deletion) — the
cascade single-node deletion performs is now stated in full.

## Context

Both deletion paths were written against the dependent tables that existed when they were written,
and neither was revisited when client-request intake (ADRs 0033/0034) added three more references
into `job_node`. Every foreign key into `job_node` is `ON DELETE RESTRICT` bar
`app_user.home_node_id`, so a reference nobody taught the cascade about does not fail at review — it
fails in a deployed database, for whichever rows happen to have a dependent, as the catch-all
`job-node-not-deletable` ("this job node cannot be deleted because it has dependent data").

Two defects reached the deployed PostgreSQL instance:

1. **`IJobNodeCommandPort.DeleteAsync` cleared `leaf_work`, `work_session` and nothing else.** A leaf
   that arrived through intake carries a `job_request` row, possibly a `node_rate_override`, and
   could anchor a `request_holding_area`. All three refused the delete with the catch-all message.
2. **`SubtreeDeletionCascade` deleted `job_request_note` rows, which ADR 0034's
   `job_request_note_no_delete` trigger refuses unconditionally.** ADR 0061 names
   `job_request_note` in its cascade order, so the two ADRs contradicted each other outright: any
   subtree containing a request with even one note was permanently undeletable, reported as the
   equally uninformative `job-node-write-rejected` ("this write violates a job-node structural
   invariant").

Neither is provider-specific — SQLite carries the identical foreign keys and the identical trigger.
Only the deployed instance had intake data, which is why only it showed the failures. No test put a
request, a note, a rate override, or a holding area in front of either delete; `JobRequestNoteEntity`
appeared in exactly one file outside the intake ports, the cascade that could not run.

## Decision

**A job node's request-intake rows are part of the node, and are destroyed with it.** A request has
no meaning without the job it became; leaving it behind would strand a `job_request` row pointing at
a node that no longer exists, which the schema forbids anyway.

- **`job_request` and `node_rate_override` are cascaded** by both deletion paths — single-node
  (`JobNodeDependentCascade`) and subtree (`SubtreeDeletionCascade`) — inside the one existing
  transaction. The audit event records what was destroyed (`destroyed_job_request`,
  `destroyed_job_request_note_count`, `destroyed_node_rate_override_count`), since nothing else
  survives to describe it.
- **`request_holding_area` is refused, not cascaded**, by name: `job-node-holding-area-anchored`,
  naming the areas. An area is configuration that outlives any one node, so it is re-anchored or
  deactivated deliberately. This is the rule ADR 0061 already applied to a subtree, now applied to a
  single node too rather than surfacing as the catch-all.
- **`job_request_note` follows its request via `ON DELETE CASCADE`**, and is never deleted directly
  by either path. ADR 0034's append-only guarantee is qualified, not abandoned: the reject-delete
  trigger now fires only while the parent `job_request` row still exists. Deleting a note on its own
  is refused exactly as before — during the cascade the database has already removed the parent, so
  the trigger's existence check finds nothing and lets the thread go. Append-only therefore means "a
  note outlives every operation except the destruction of the request it belongs to", which is the
  only reading compatible with ADR 0061.

**A dependent table without a declared disposition is a test failure, not a production failure.**
`JobNodeDependentTableCoverageTestsBase` reads the deployed schema's own catalogue — every foreign
key into `job_node`, and every delete-blocking trigger on a table in the deletion closure — and holds
it against a declared table of dispositions (cascaded, refused, structural, cleared by the database).
Adding a reference into `job_node`, or a trigger that can refuse a delete, fails that test until
someone decides which it is; the behaviour itself is then proven per disposition in
`JobNodeCommandPortContractTestsBase`, against both providers.

## Consequences

- Deleting a request-anchored job destroys the requester's thread. That is deliberate and audited,
  but it is not recoverable: intake history for that job is gone, as ADR 0061 already accepted for
  subtree deletion.
- `job_request_note`'s foreign key changes from `RESTRICT` to `CASCADE` in both providers' schema
  version scripts, and the reject-delete trigger becomes conditional. Pre-release, these are edited
  in place (ADR 0011 binds only after first deployment) — **the deployed database must be
  rebuilt**, not migrated.
- The catch-all translations in `JobNodeWriteExceptionTranslation` remain the last line of defence,
  but every case reachable by ordinary use now has a named category ahead of them. A catch-all
  message in production should be read as a missing disposition, not a user error.
