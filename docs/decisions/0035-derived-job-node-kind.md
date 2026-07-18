# ADR 0035: Derived job-node kind from structure

**Status:** Accepted
**Closes:** `docs/plans/2026-07-12-derived-node-kind-plan.md` Stages 0–7; supersedes any wording
that treats `job_node.kind_id`, `node_kind`, or `request_holding_area.default_kind_id` as persisted
configuration.

## Context

`job_node.kind_id` (Root/Branch/Leaf) was stored once at creation (`AddBranchAsync`/`AddLeafAsync`)
and reconciled in exactly one place — `DecomposeWorkedLeafAsync`, which flips a worked leaf's `Kind`
to `Branch`. Nothing else kept it honest. Empirical testing against a live database (in a rolled-back
transaction) showed a child can be inserted directly under a childless, workless "Leaf"-labelled
node; the triggers in `0006_leaf-work-and-exclusivity.sql` only check for actual `leaf_work`/child-row
existence, never `kind_id` — so the parent's stored `kind_id` was left silently wrong.

The domain layer already modelled this correctly but was unused in production:
`NodeClassifier` derives `NodeKind` purely from structure (`ParentId is null` → Root; has children →
Branch; else → Leaf) and was referenced only by its own unit test. Every production read/write path
used the independently-stored `kind_id` instead.

This also closed a genuine UX gap: a node could never gain its first child once created as a "Leaf",
because the UI only offered "add a child" to nodes already labelled "Branch".

## Decision

Remove node kind as command input or persisted state. `Root`/`Branch`/`Leaf` remain only as a
contextual read label for compatibility with existing result shapes and UI language:

- `ParentId is null` → `Root`;
- otherwise, at least one child points to the node → `Branch`;
- otherwise → `Leaf`.

`leaf_work` is not part of the label derivation. It is a separate capability/invariant: a node with
children cannot hold `leaf_work`, and a node with `leaf_work` cannot gain children. Users always
create the same thing first — a job node. If they attach work to it, it remains a leaf. If they
create child nodes under it, it becomes apparent as a branch. The UI must not ask users whether they
are creating a branch or a leaf.

No new "undetermined" state and no `NodeKind` enum change are needed. A childless non-root node
already resolves to `Leaf` under the existing classifier. The label is apparent from surrounding
structure, not an attribute chosen at creation time.

**Schema:** drop `job_node.kind_id`, `request_holding_area.default_kind_id`, and the orphaned
`node_kind` reference table.

**Commands:** replace `AddBranchAsync`/`AddLeafAsync` with a single `AddChildAsync(CreateJobNodeRequest)`.

**Reads:** derive `Kind` at projection time from `parent_id` and child existence; expose
`HasChildren` and `HasLeafWork` as structural/capability facts for UI gating. `AttachLeafWorkAsync`
gates on "node already has children" rather than stored kind. `DecomposeWorkedLeafAsync` drops kind
writes; operation-specific audit narratives (leaf→branch) remain where the domain event explicitly
describes a contextual transition. Generic audit snapshots drop `"kind"` because it is not row
content.

**UI:** one "Create child" action whenever `HasLeafWork` is false; Work/Decompose/Achievement stay
gated on `HasChildren == false`.

## Rationale

- A stored label with no enforcement path is worse than no column — it silently diverges from
  structure while triggers and readiness calculators already work on structural facts.
- Deriving at read time matches the domain classifier already present and eliminates reconciliation
  writes after decompose or ad hoc child insertion.
- Single creation action removes a choice users never needed to make and fixes the "can't add first
  child to a leaf" UX gap.
- `HasChildren`/`HasLeafWork` on read DTOs keep capability gating explicit rather than inferring
  behaviour from a derived label.

## Consequences

- **Breaking API change:** `IJobCommands.AddBranchAsync`/`AddLeafAsync` → `AddChildAsync`; recorded
  in `PublicAPI.Unshipped.txt` and client samples.
- **Schema:** `kind_id`, `default_kind_id`, and `node_kind` removed from both providers' schema
  versions (pre-release in-place edit per ADR 0011 carve-out).
- **Audit:** generic `SnapshotJobNode` payloads no longer include `"kind"`; decompose retains its
  leaf→branch narrative fields.
- **Performance:** browse/search/ancestor projections must derive kind set-based (no per-row N+1);
  provider-specific computed projections acceptable if correlated `EXISTS` probes regress scale
  fixtures.
- **Docs:** `docs/database-entities.md`, API/reference docs, and `CLAUDE.md` house style updated to
  describe derived labels; ADR 0021's mention of `node_kind` as reference data is historical only
  after this ADR lands.
