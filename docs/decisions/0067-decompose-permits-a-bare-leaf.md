# ADR 0067: Decompose permits a bare leaf, not only a worked one

**Status:** Accepted
**Amends:** spec §4.5 (codex) / §3.5 (claude) — "Decomposing a leaf after work has begun" now covers a
leaf before work has begun too. `IJobCommands.DecomposeWorkedLeafAsync`'s public contract widens; ADR
0065 permits this without a compat shim.

## Context

`/Jobs/Decompose` and the command behind it (`DecomposeWorkedLeafAsync`) only ever supported one case:
a leaf that already has `LeafWork` attached splits into a child holding the existing work plus the
newly identified siblings. A leaf with no `LeafWork` attached ("bare") failed the same
`leaf-work-not-attached` check as an already-branch node, and the Web page withdrew the form with "This
job has no recorded work to carry over, so there is nothing to decompose. Add children to it directly
instead."

That refusal is unnecessarily strict for a bare leaf. Splitting a job into its known pieces before any
work starts on it is a legitimate, and probably common, ordering — the reader wants to decompose the
*concept* of the original job, whether or not work has begun. Requiring "Add children to it directly
instead" costs nothing functionally but loses the reason the reader reached for Decompose in the first
place: naming several children at once against one branch description, in one form.

An already-branch node (a node that already has children) still cannot be decomposed — leaf/branch
exclusivity (spec §4.2) already forbids a node holding both children and `LeafWork`, and a branch
doesn't fit the "one leaf's work becomes one child" shape decompose is built around.

## Decision

Extend the existing `DecomposeWorkedLeafAsync` rather than add a new method or page. The Web page
already treats this as one operation with one form; branching its behaviour on whether the node
currently holds `LeafWork` keeps the API and the page single-shaped:

- **Worked leaf** (has `LeafWork`): unchanged behaviour — the existing work becomes a new child, the
  named children become its siblings, the node becomes their branch parent.
- **Bare leaf** (no `LeafWork`, no children): converts directly into a branch holding exactly the
  named new children. No existing-work child is created; nothing to move. At least one named child is
  required — a decompose into zero children would leave the node in neither a leaf nor a branch state.
- **Already a branch** (has children): still rejected outright, now via its own constraint id rather
  than incidentally sharing `leaf-work-not-attached`.
- **Root**: still rejected (defensive parity with `AttachLeafWorkAsync`'s root guard; the root can
  never be bare in practice, but the check costs nothing).

New `InvariantViolationException` constraint ids on `DecomposeWorkedLeafAsync` (both providers,
`JobNodeCommandPort` — this port pair stays duplicated per ADR 0064, so both need the same edit):

- `job-node-has-children-cannot-decompose` — mirrors `LeafWorkAttachSupport`'s
  `job-node-has-children-cannot-attach-leaf-work`.
- `job-node-is-root-cannot-decompose` — mirrors `job-node-is-root-cannot-attach-leaf-work`.
- `job-node-decompose-requires-a-child` — a bare leaf named zero new children.

`leaf-work-not-attached` is no longer a possible outcome of this command.

### Contract shape

`DecomposeWorkedLeafRequest.ExistingWorkDescription` becomes optional (`string?`) — meaningful, and
validated as required, only when the leaf currently has `LeafWork`; a caller supplying it for a bare
leaf or omitting it for a worked leaf gets an `ArgumentException` (usage error, not a domain
invariant). `DecomposeWorkedLeafResult.ExistingWorkChildId` becomes `JobNodeId?` — `null` for a
bare-leaf decompose.

### Web page

`/Jobs/Decompose` drops its bare-leaf pre-refusal; only "already has children" withdraws the form now.
The "What moves onto the new child" card and the `ExistingWorkDescription` field render only when the
node currently has `LeafWork`. The client-side validation mirrors the command's own: the description
field is required when there's work to describe, and at least one named child is required when there
isn't.

## Consequences

- `DecomposeWorkedLeafRequest`/`Result`'s public shape changes (two members become nullable). No
  external consumer to protect (ADR 0065); `PublicAPI.Shipped.txt` is edited in place, not shuffled
  through `Unshipped.txt`, matching the pre-release convention already used for schema versions.
- The `FakeJobNodeCommandPort` used by `JobCommands` unit tests mirrors the same branching so
  Application-layer tests exercise the same three-way split (worked / bare / already-branch) the real
  ports do.
- `Concurrent_decomposes_of_the_same_leaf_allow_exactly_one_to_succeed`'s SQLite loser now typically
  fails with `job-node-has-children-cannot-decompose` instead of the old `leaf-work-not-attached` (it
  finds the leaf already converted into a branch, rather than finding no `LeafWork`); the test only
  asserts mutual exclusion via a generic `JobTrackException` catch, so this needed no assertion change.
