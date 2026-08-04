# Significant code-review remediation plan

**Date:** 2026-08-04
**Status:** Implemented (2026-08-04)
**Scope:** Significant correctness and transaction-boundary findings in changes landed after the
2026-08-01 security-audit remediation baseline. Cosmetic, naming, and local maintainability issues
are intentionally excluded.

## 1. Assessment

No critical issue was found. Three material defects remain: two in requester auto-acknowledgement
and one in the Admin CLI's import-plus-home-node workflow. They can cause a valid business command
to roll back under ordinary concurrency, persist an `InProgress` requester job without the accepted
acknowledgement/audit side effect, or leave one CLI operation only partly applied.

Severity order is **High** > **Medium** > **Low**.

## 2. Findings

### 2.1 Concurrent first-work operations make the supposedly idempotent auto-acknowledgement fail

| | |
|---|---|
| **Severity** | **High** |
| **Evidence** | `src/JobTrack.Persistence.Shared/RequesterRequestAutoAcknowledgement.cs:34-51`; `src/JobTrack.Persistence.Shared/JobTrackModelConfiguration.cs:434-451`; the `DbUpdateConcurrencyException` translations in both achievement and work-session command ports |

`AcknowledgeIfNeededAsync` reads the owning `job_request`, checks `AcknowledgedAt`, mutates the
tracked entity, and increments its concurrency-tokened `RowVersion`. Two transactions starting work
on different leaves under the same unacknowledged request can both read the old version. One commits;
the other updates zero rows and receives `DbUpdateConcurrencyException`, rolling back the otherwise
independent work/outcome command. The enclosing ports then report a leaf/session concurrency conflict,
even though neither leaf nor session version necessarily moved.

That contradicts ADR 0058's required "silent, idempotent no-op" and the Work-page rule that a
concurrency message must mean the relevant version actually moved. The current provider contract
tests cover sequential already-acknowledged calls but no simultaneous first-acknowledgement race.

### 2.2 Create-and-start omits requester auto-acknowledgement

| | |
|---|---|
| **Severity** | **High** |
| **Evidence** | `src/JobTrack.Persistence.PostgreSql/PostgreSqlJobNodeCommandPort.cs:905-972`; equivalent SQLite implementation; ADR 0058 |

`CreateJobNodeRequest.BeginWork` creates `LeafWork`, advances it from `Waiting` to `InProgress`, and
opens a session in one transaction, but neither provider calls
`RequesterRequestAutoAcknowledgement.AcknowledgeIfNeededAsync`. Creating and immediately starting a
child anywhere below an unacknowledged request therefore leaves that request unacknowledged and emits
no `auto-acknowledge-request` audit event.

This is not merely a missing display update. The composite claims to leave the same state and audit
trail as `StartWorkAsync`, but it has drifted from that canonical transition. A later session start
does not repair the omission because `StartWorkAsync` invokes auto-acknowledgement only while it is
performing the `Waiting -> InProgress` transition.

### 2.3 Importing a flagged home node is not one atomic operation

| | |
|---|---|
| **Severity** | **Medium** |
| **Evidence** | `src/JobTrack.AdminCli/JobTreeImportCommand.cs:90-140` |

The CLI first commits `ImportSubtreeAsync`, then calls `SetHomeNodeAsync` once per target account with
a separate transaction and correlation identifier. If any later call fails or the process is
interrupted, the tree remains imported and any earlier accounts remain changed while later accounts
do not. Resolving usernames and validating the flagged row before the import removes predictable
input failures, but cannot make the workflow atomic: account state can change concurrently, a write
can fail, and the process can stop between calls.

This directly conflicts with the repository's compound-write rule (one ACID transaction, never a
workflow split across multiple `IJobTrackClient` mutations). It also makes one operator intent appear
as unrelated correlations and requires the CLI to act as each target account rather than preserving
one authoritative actor across the operation.

## 3. Remediation sequence

### 3.1 Make auto-acknowledgement atomic and race-idempotent

1. Add a shared provider contract race test first: two independent connections simultaneously start
   work or record terminal outcomes on different leaves under one unacknowledged request. Both
   triggering commands must succeed, the request must be acknowledged once, and exactly one
   `auto-acknowledge-request` event must exist.
2. Add the PostgreSQL-specific race test with a deterministic barrier around the request-row
   admission point. Add the equivalent serialized-contention proof for SQLite.
3. Replace the tracked read/check/update with one provider-appropriate atomic conditional update
   (`acknowledged_at IS NULL`) that returns whether this transaction won. Queue the audit event only
   for the winner. Keep the mutation and audit event inside the triggering command's existing
   transaction.
4. On PostgreSQL, author the operation EF-first. If EF cannot return the affected request identity
   safely in one statement, encapsulate the irreducible SQL as a source-controlled stored function
   invoked through EF; do not add an inline raw-SQL string beside the helper. Keep SQLite's minimal
   conditional statement/provider implementation equivalent.
5. Preserve nearest-request-anchor semantics explicitly rather than relying on unordered
   `FirstOrDefaultAsync` over an ancestor-id set. Add a nested-anchor contract case if nested request
   anchors are valid; otherwise add and test the database invariant that makes them impossible.

### 3.2 Route every first-work composite through the same side effect

1. Add shared job-node command-port tests before implementation for creating and beginning work on a
   child below an unacknowledged request. Assert the request fields, exactly one auto-ack audit event,
   one correlation identifier across create/attach/advance/ack/session events, and full rollback on
   an injected acknowledgement failure.
2. Invoke the race-safe helper from both providers' `BeginWorkOnNewNodeAsync` inside the existing
   create transaction, immediately after the `Waiting -> InProgress` transition.
3. Add an architecture/coverage guard around every code path that creates an `InProgress` or
   terminal leaf, so a future composite cannot bypass ADR 0058 by reproducing the transition locally.
   Prefer extracting the shared transition orchestration over maintaining another call-site list.

### 3.3 Make import plus home assignments one library command

1. Add a shared contract test first for a subtree import carrying a flagged local node id and a set
   of target users. Inject a failure on the final assignment and prove that no node, prerequisite,
   home-node change, or audit event commits. Add PostgreSQL and SQLite contention tests against a
   target account-state transition.
2. Extend the database/application import contract so the home-node assignments are resolved from
   the import's local-id map and written inside `ImportSubtreeAsync`'s existing explicit transaction.
   Validate every target account and the derived branch kind under the same transaction before any
   commit.
3. Define authorization deliberately: one authenticated operator is the actor for the entire
   operation, while target user ids are affected entities. Do not synthesize a `CommandContext` for
   each target user. Record all events under the import's single correlation id.
4. Reduce `JobTreeImportCommand` to parsing/resolution plus one `IJobTrackClient` mutation. Remove the
   post-commit assignment loop and the partial-success message because partial success is no longer a
   valid outcome.
5. Update `docs/operations/job-tree-import.md` to state and demonstrate the expanded all-or-nothing
   guarantee.

## 4. Verification and delivery gates

Implement in database-contract -> reusable-library -> host order. Each slice follows failing test,
smallest implementation, then refactor. Before committing each slice run the repository commit gate
and the targeted provider classes touched by that slice through `gtimeout`. The final remediation
commit must additionally run the full solution suite because this plan crosses database, library,
and host boundaries; run the performance lane only if the conditional auto-ack query changes a
measured query.

Completion requires:

- both providers prove simultaneous first-work operations succeed with one acknowledgement/audit;
- every supported first-work/terminal composite is covered by auto-ack contract tests;
- import plus all requested home-node assignments has one transaction, actor, correlation id, and
  all-or-nothing failure behaviour;
- the authoritative ADR/operational documentation reflects the final command shapes; and
- this plan's status and `docs/plans/README.md` are updated together.

## 5. Delivery record

All three findings are remediated, one commit per section, each gated as §4 requires.

- **§3.1** — `RequesterRequestAutoAcknowledgement` now performs one conditional
  `UPDATE ... WHERE acknowledged_at IS NULL` (EF `ExecuteUpdateAsync`) inside the triggering
  command's transaction, queueing the audit event only for the transaction whose statement affected
  the row. Anchor resolution moved to `JobNodeHierarchyQueries.GetNearestRequestAnchorIdAsync`,
  ordered by distance. Nested anchors are permitted by schema version 0020 and are now a contract
  case rather than a new database invariant. Per-provider races (simultaneous first work,
  simultaneous terminal outcomes) reproduce the original `DbUpdateConcurrencyException` before the
  fix and pass after it.
- **§3.2** — the shared unit became the transition itself, not the acknowledgement call:
  `Persistence.Shared.LeafAchievementTransition.ApplyAsync` owns the mutation, version bump,
  `set-achievement` audit event, and acknowledgement together, and all eight prior call sites plus
  both providers' `BeginWorkOnNewNodeAsync` route through it.
  `LeafAchievementTransitionArchitectureTests` enforces that nothing else in either provider
  reassigns a tracked `LeafWorkEntity.Achievement` or calls the acknowledgement helper — the
  "extract the orchestration rather than maintain a call-site list" option in step 3.
  - **Beyond the plan's scope, decided during delivery:** that guard also surfaced
    `ImportSubtreeAsync`, which writes an imported leaf's final achievement directly and never
    acknowledged. It now does, via `ApplyImportedAsync`, keeping the import's own single
    `import-leaf-work` audit event. ADR 0058 records the widened trigger set.
- **§3.3** — `ImportSubtreeRequest` gained `HomeNodeLocalId`/`HomeNodeUserIds`; the assignments are
  resolved from the import's local-id map and written by `ImportHomeNodeAssignment` inside the
  import's existing transaction, under the operator's single actor and correlation identifier, with
  one `set-home-node` audit event per affected account. `JobTreeImportCommand` is now parsing plus
  one `IJobTrackClient` mutation, and the partial-success message is gone.

Not applicable: the performance lane. The conditional acknowledgement query is a single-row primary
key update and the anchor walk replaced an equivalent recursive walk, so no measured query changed
shape.

### 5.1 Post-implementation review follow-ups

- `c3917c03` closes the missing contention evidence: PostgreSQL now holds both terminal transitions
  at the request-row update boundary before either proceeds, and both providers exercise a real
  concurrent disable of a home-node target. Imports lock their complete identity-row set in stable id
  order, then reject a disabled or locked target inside the import transaction.
- `25da4a79` makes affected home-node account ids unique at the public application boundary. The CLI
  canonicalizes repeated usernames case-insensitively, so each affected account is assigned, audited,
  and reported once.

The post-follow-up full solution suite passed all 3,684 tests. The performance lane remains not
applicable for the reason recorded above.
