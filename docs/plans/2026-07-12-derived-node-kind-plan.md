# Remove stored job-node kind; derive contextual labels from structure

**Date:** 2026-07-12
**Status:** Implemented (ADR 0035 accepted).
**Closes with:** a new ADR (next number: **0035**), drafted as Stage 0 below.

## 1. Problem

`job_node.kind_id` (Root/Branch/Leaf) is a stored column, set once at creation
(`AddBranchAsync`/`AddLeafAsync`) and reconciled in exactly one place — `DecomposeWorkedLeafAsync`,
which flips a worked leaf's `Kind` to `Branch`. Nothing else keeps it honest. Verified empirically
against `jobtrack_live` (in a rolled-back transaction, no data changed): a child can be inserted
directly under a childless, workless "Leaf"-labelled node. The two triggers in
`0006_leaf-work-and-exclusivity.sql` only check for actual `leaf_work`/child-row *existence*, never
`kind_id` — so the parent's stored `kind_id` is left silently wrong (still "Leaf" despite now having
a child).

The domain layer already models this correctly and doesn't use the model in production:
`src/JobTrack.Domain/Hierarchy/NodeClassifier.cs` derives `NodeKind` purely from structure
(`ParentId is null` → Root; has children → Branch; else → Leaf) and is referenced nowhere except its
own unit test (`NodeClassifierTests.cs`). Every production read/write path uses the
independently-stored `kind_id` instead.

## 2. Decision

Remove the idea of node kind as command input or persisted state. `Root`/`Branch`/`Leaf` remain only
as a contextual read label for compatibility with existing result shapes and UI language:

- `ParentId is null` → `Root`;
- otherwise, at least one child points to the node → `Branch`;
- otherwise → `Leaf`.

`leaf_work` is not part of the label derivation. It is a separate capability/invariant: a node with
children cannot hold `leaf_work`, and a node with `leaf_work` cannot gain children. Practically, users
always create the same thing first — a job node. If they attach work to it, it remains a leaf. If they
create child nodes under it, it becomes apparent as a branch. The UI must not ask users whether they
are creating a branch or a leaf.

No new "undetermined" state and no `NodeKind` enum change are needed for this slice. A childless
non-root node already resolves to `Leaf` under the existing classifier. The important change is that
the label is apparent from surrounding structure, not an attribute chosen at creation time.

This also closes a genuine UX gap discovered while designing this plan: today, a node can never gain
its first child once created as a "Leaf", because the UI only offers "add a child" to nodes already
labelled "Branch". Under the derived model, "create child" is offered to any node that does not hold
`leaf_work`; "Work"/"Achievement"/"Decompose" are offered only for childless nodes.

## 3. Full-repo inventory (confirmed via code search, not assumed)

- **No raw SQL** references `kind_id`/`node_kind` anywhere in `src/` — the job_node kind path is pure
  EF LINQ. Low risk of missed call sites.
- **No `WHERE kind_id = …` filter** exists anywhere — every current usage is a *projection* of
  `.Kind` into a result DTO (`ToResult`, `SnapshotJobNode`, `ProjectToSummaries`,
  `LoadAncestorsAsync`), not a query filter. Readiness/awaiting-progress/achievement calculators
  already work structurally (children/`leaf_work` existence), never touching `Kind`. This confines
  the real rewrite to a handful of projection call sites, not query semantics.
- **`AttachLeafWorkAsync`** is the one place that *gates* behavior on stored `Kind`
  (`if (node.Kind != NodeKind.Leaf) throw`) — becomes a structural "node already has children" check,
  mirroring the DB trigger's own `leaf_work_node_is_leaf_on_insert` (the same deferred-validation
  pattern already used elsewhere in this codebase: app-level preflight check, DB trigger
  authoritative).
- **`request_holding_area.default_kind_id`** currently exists only to choose the stored kind of
  future requester-submitted nodes. Under this decision, creation no longer accepts or stores a kind,
  so `default_kind_id` becomes dead configuration. Drop it in the same schema/documentation slice
  rather than leaving a setting nothing observes.
- **`node_kind`** becomes orphaned once both `job_node.kind_id` and
  `request_holding_area.default_kind_id` are gone. Drop the lookup table and remove public wording
  that treats enum numeric values as database reference-table ids.
- Roughly **20 test files** (contract/integration/e2e/performance) insert explicit `kind_id` values
  in raw seed SQL. Same mechanical fix everywhere: stop supplying it (column gone); assertions on
  `.Kind` in results need no change since the derived value has identical enum semantics.

## 4. Architectural constraints (per CLAUDE.md)

- **Layer order**: database → domain/library → web. Each stage's gate passes before the next starts.
- **Pre-release schema editing**: edit `0004_job-node-and-priority.sql` (both providers) in place —
  nothing has shipped, ADR 0011's forward-only rule doesn't bind yet.
- **Public-API discipline**: `IJobCommands.AddBranchAsync`/`AddLeafAsync` → single `AddChildAsync` is
  a breaking change to `JobTrack.Application`'s public surface. `NodeKind` remains in read DTOs for
  now, but its XML docs change from "stored kind/reference id" to "derived contextual label". Review
  against `Framework_Design_Guidelines_Essentials.md`; record the break in ADR 0035.
- **Performance discipline**: deriving the label must not regress PostgreSQL browse/search paths into
  per-row N+1 probes. A computed/projection field is acceptable if it is genuinely computed from
  structural facts (`parent_id`, child existence, `leaf_work` existence for capability flags) and not
  caller-maintained cached state. Prefer set-based LINQ/SQL translation with supporting indexes first;
  if a database-level computed projection becomes necessary, add it as provider-owned schema
  infrastructure with tests proving it cannot drift from the underlying relationships.
- **TDD per slice**: shared contract test → PostgreSQL → SQLite → provider-specific, failing test
  first, for every schema-touching stage.

## 5. Staged work breakdown (each stage its own commit, commit gate per CLAUDE.md)

### Stage 0 — Draft ADR 0035
`docs/decisions/0035-derived-job-node-kind.md`: Context (the stale-`kind_id` gap, found via live
empirical test), Decision (drop stored/configured node kind, derive contextual labels from parent and
child existence, single create action), Consequences (breaking `AddChildAsync` API change,
`request_holding_area.default_kind_id`/`node_kind` removal, generic audit snapshots drop `"kind"`
because it is not row content), Status: Accepted once this plan lands.

### Stage 1 — Schema: drop node-kind storage/configuration (database layer, both providers)
- `database/{postgresql,sqlite}/schema-versions/0004_job-node-and-priority.sql`: remove `kind_id`
  column + its FK to `node_kind`.
- `database/{postgresql,sqlite}/schema-versions/0014_department-and-request-holding-area.sql`: remove
  `request_holding_area.default_kind_id`; requester-submitted nodes are created as ordinary child
  nodes and only later appear as leaves or branches based on structure.
- `database/{postgresql,sqlite}/schema-versions/0001_schema-version-and-reference-tables.sql`: remove
  `node_kind` once no table references it.
- Update every contract-test seed helper across `tests/JobTrack.Database.ContractTests/*` (11 files
  per the inventory: `LeafWorkSchemaContractTestsBase`, `JobNodeSchemaContractTestsBase`,
  `JobRequestSchemaContractTestsBase`, `WorkSessionSchemaContractTestsBase`,
  `HierarchyMoveSchemaContractTestsBase`, `PostgreSqlRoleGrantsTests`, etc.) to stop inserting
  `kind_id` and to stop supplying `default_kind_id` for holding areas. Re-run the existing
  exclusivity tests unchanged — they already prove the trigger behavior the dropped column never
  enforced anyway.
- Order: shared contract test → PostgreSQL → SQLite, per the usual slice discipline.

### Stage 2 — `JobNodeEntity`/EF mapping: remove stored `Kind`
- `src/JobTrack.Persistence.Shared/Entities/JobNodeEntity.cs`: remove the `Kind` property.
- `src/JobTrack.Persistence.Shared/Entities/RequestHoldingAreaEntity.cs`: remove `DefaultKind`.
- `JobTrackModelConfiguration.ConfigureJobNode`: remove the `kind_id` column mapping.
- `JobTrackModelConfiguration.ConfigureRequestHoldingArea`: remove the `default_kind_id` mapping.
- Add the structural derivation at the three flagged read call sites (both providers,
  `SqliteJobBrowseQueryPort`/`PostgreSqlJobBrowseQueryPort`): `ProjectToSummaries` (reuse its existing
  `HasChildren` EXISTS pattern directly), `ToResult` (single-node detail — needs child-existence and
  `leaf_work`-existence flags loaded with the node), `LoadAncestorsAsync` (ancestor breadcrumb —
  extend the existing ancestor-chain query rather than adding one EXISTS per ancestor, to avoid N+1).
- PostgreSQL performance check: inspect the generated SQL/plan for browse/search/ancestor projections
  after adding derived fields. If repeated correlated `EXISTS` probes become a measurable problem on
  the scale fixtures, introduce a provider-specific computed projection (for example a mapped view or
  equivalent set-based query shape) rather than restoring a writable `kind_id` column.
- Add explicit read-model facts needed by the UI: `JobNodeResult.HasChildren` and
  `JobNodeResult.HasLeafWork` (or equivalently named fields). `Kind` remains a derived label, but UI
  gating must use these structural/capability facts rather than infer behavior from `Kind`.

### Stage 3 — Command ports: unify creation, restructure `AttachLeafWorkAsync`, drop Decompose's writes
- `IJobCommands`: replace `AddBranchAsync`/`AddLeafAsync` with `AddChildAsync(CreateJobNodeRequest)`.
- `SqliteJobNodeCommandPort`/`PostgreSqlJobNodeCommandPort`: single `CreateAsync`, no `NodeKind`
  parameter; audit event type collapses from `create-branch`/`create-leaf` to one `create-job-node`.
- `AttachLeafWorkAsync`: `node.Kind != NodeKind.Leaf` → "node already has children" structural check;
  new invariant id (e.g. `job-node-has-children-cannot-attach-leaf-work`).
- `DecomposeWorkedLeafAsync`: drop the `branch.Kind = NodeKind.Branch` / child `.Kind = NodeKind.Leaf`
  writes (nothing to write); keep the audit before/after `"kind"` literals as-is (this operation is
  unconditionally leaf→branch, no query needed).
- `SnapshotJobNode`: drop `"kind"` from the generic audit before/after payload. It is a derived label,
  not stored row content. Keep operation-specific audit fields where the domain event explicitly
  describes a contextual transition, such as `decompose-worked-leaf`'s leaf→branch narrative.
- `SqliteJobRequestCommandPort`/`PostgreSqlJobRequestCommandPort.SubmitAsync`: stop setting
  `Kind = holdingArea.DefaultKind`; the holding area no longer has a default kind.
- `SqliteInstallationBootstrapPort`/`PostgreSqlInstallationBootstrapPort`: drop the explicit
  `Kind = NodeKind.Root` on the bootstrap root (trivially derived from `ParentId is null`).
- Update `JobTrack.Application/PublicAPI.Unshipped.txt`, the fake command port, and XML docs that
  currently describe a branch/leaf creation split.

### Stage 4 — Web/API layer: one "Create child" action, structural button gating
- `Pages/Jobs/Create.cshtml(.cs)`: drop the `Kind` route/bind property; call `AddChildAsync`.
- `Pages/Jobs/Browse.cshtml`: replace every "New branch"/"New leaf" pair — the root-scoped pair added
  in the immediately preceding commit, and the per-node Branch-only pair — with one "Create child"
  link. Show it whenever `HasLeafWork` is false. Work/Decompose/Achievement stay gated on
  `HasChildren == false` (and their existing command-side checks remain authoritative).
- Update `JobTrackApi` response mapping/OpenAPI result types so any returned `kind` is documented as
  a derived label; expose `hasChildren`/`hasLeafWork` where the Razor UI or external clients need
  capability decisions.
- Update `samples/JobTrack.UatSeed` and `samples/JobTrack.ExternalApiClient` for the renamed command
  surface and the derived-kind response semantics.

### Stage 5 — Wider test cleanup
- Same mechanical fix as Stage 1 across `tests/JobTrack.Web.IntegrationTests`,
  `tests/JobTrack.Web.EndToEndTests`, `tests/JobTrack.Database.PerformanceTests`,
  `tests/JobTrack.TestSupport`: stop inserting `kind_id` in seed helpers and stop inserting
  `default_kind_id` for holding areas.
- `JobTrackModelConfigurationTests` (persistence-shared): update the expected-mapping-shape assertion.
- Performance generator/tests that currently count or select by `kind_id` must switch to structural
  predicates (`parent_id IS NULL`, child existence, child absence), so they continue proving the same
  scale shape without relying on the removed column.

### Stage 6 — Reference docs: bring the entity/spec docs in line
- `docs/database-entities.md:27`: the `job_node` column table currently lists `kind_id` as a stored
  FK column — remove that row and add a short note next to the leaf/branch exclusivity rules (§48-53)
  that Root/Branch/Leaf is a contextual label derived from parent/child structure, not stored,
  pointing at ADR 0035. Also remove `request_holding_area.default_kind_id` (`:151`).
- `docs/jobtrack_spec_claude.md:931`: its schema sketch shows `kind int NOT NULL REFERENCES
  node_kind(id)` on `job_node` — review this section and correct it to describe the derived model
  (or add a note that it's now derived, if the sketch is meant to stay illustrative rather than
  literal). `jobtrack_spec_codex.md` doesn't describe `job_node.kind` at column level, so likely needs
  no change — confirm during implementation rather than assuming.
- `docs/api/jobtrack-client-design.md`, `docs/api/external-http-api-reference.md`, requester-intake
  docs, and ADR 0033 follow-up notes: remove wording that says callers/configuration choose a node
  kind; describe all node creation as ordinary child-node creation followed by either attaching work
  or creating child nodes.
- `README.md:6-7`'s one-line tree description ("branches and leaves") stays accurate as-is; no change
  needed there.
- `docs/plans/README.md`: add this plan's row (done as part of Stage 0/finalizing this doc).

### Stage 7 — CLAUDE.md: document the design principle
Add a bullet to the "House style" section (explicitly requested): a node's classification (or any
similar structural label) is derived from real relationships at read time, never cached in a column
nothing keeps in sync — link to ADR 0035 as the worked example.

## 6. Verification

- Full commit gate per stage (`fast-test.sh --build` + targeted `--filter`); full solution suite once
  at the end of Stage 7.
- Add automated provider-backed regression coverage for the empirical stale-kind bug: create a
  childless node, add a child under it, then verify the parent is returned as `Branch` immediately
  because the label is derived from child existence.
- Add UI/integration coverage that the create page has no branch/leaf selector and Browse exposes
  one "Create child" action for a workless childless node.
- Manually re-run the exact empirical repro from the design conversation (insert a child under a
  childless node, confirm the parent's *displayed* kind in `/Jobs/Browse` correctly reads as Branch
  immediately afterward — no stale label possible since there's no stored label left to go stale).
- `docs/plans/README.md` row and ADR 0035 status flip to `Accepted` once merged.
