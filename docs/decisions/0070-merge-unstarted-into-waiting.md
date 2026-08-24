# ADR 0070: Merge the Active column's Unstarted state into Waiting

**Status:** Accepted
**Amends:** plan §2.4 (the Active-column idle-state vocabulary) and the `ActiveIdleStatus` enum in
`ActiveSessionSummaryModel`. No effect on the requester-facing `RequesterStatus` vocabulary (ADR 0034)
or the internal `Achievement` states (ADR 0001).

## Context

The staff Active column (`_ActiveSincePill`, `_ActiveStatusIcon`) showed two distinct idle pills for an
open leaf nobody was working:

- **Unstarted** — the leaf has no `leaf_work` record yet (`Achievement` null, no session history).
- **Waiting** — the leaf has a `leaf_work` record sitting at its initial `Achievement.Waiting`, but no
  session yet.

The split mirrored an internal fact — whether a `leaf_work` row exists — not anything the user acts on.
Both pills sit in the same column, both mean "open, nothing logged yet", and both offer the same Start
affordance on every leaf row. Critically, `WorkSessionCommandPort.StartWorkAsync` **auto-attaches
`leaf_work` when it is missing** (`leafWork ??= await LeafWorkAttachSupport.CreateAsync(...)`), so
clicking Start on an Unstarted leaf and on a Waiting leaf does the identical thing. The only real
difference — optional `PartialCriteria`/`FullCriteria` on the `leaf_work` row — is not shown in the pill
and is editable at any time.

Two labels for one user-facing state is noise. It also invites the wrong mental model: that Unstarted is
"not workable yet" and needs a setup step first, which is false.

## Decision

Collapse `Unstarted` into `Waiting`. An open leaf that is not archived, not paused, and not an
unacknowledged request reads **Waiting** whether or not a `leaf_work` record exists yet.

- `ActiveIdleStatus.Unstarted` is removed from the enum.
- `ActiveSessionSummaryModel.IdleStatus` returns `Waiting` when `Achievement is Achievement.Waiting`
  **or** the `Unstarted` input flag (`!HasSessionHistory`) is set; otherwise `null` (branch, or a state
  with no pill).
- The `Unstarted` property stays as the input flag carrying "no session history"; it now feeds the
  Waiting pill rather than its own.
- `_ActiveSincePill.cshtml` and `_ActiveStatusIcon.cshtml` drop the Unstarted switch arm.
- `site.css` drops the now-unreferenced `.status-pill-inactive` rule; the `.status-pill-waiting` and
  `.status-pill-unack` comments are updated.

**Unacknowledged stays a distinct pill.** It means something the user acts on differently — a request
they have not yet accepted — and keeps its own blue treatment and its precedence over Waiting.

## Consequences

- One fewer state in the Active-column vocabulary. `Subtree_leaf_rows_show_waiting_for_open_leaves_and_a_distinct_pill_for_unacknowledged_work`
  (renamed from `..._distinguish_unstarted_waiting_and_unacknowledged_open_work`) now asserts a leaf with
  no `leaf_work` and a leaf with a bare `leaf_work` both render the single Waiting pill.
- No database, port, or `IJobTrackClient` change — this is display vocabulary only. `leaf_work`
  existence remains a real distinction in the persistence layer; it just stops surfacing as a separate
  user-facing status.

### Follow-up (not settled here)

`/Jobs/Work` still guards a no-`leaf_work` leaf on its Complete/Reopen/Change-outcome handlers ("This
leaf has no work attached") while Browse's one-click Start silently auto-attaches. That asymmetry is
worth aligning to one policy, but it is a behaviour change on the command surface, out of scope for this
display-only merge. Track it separately.
