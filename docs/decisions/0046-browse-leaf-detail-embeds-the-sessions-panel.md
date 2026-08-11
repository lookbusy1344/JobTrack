# ADR 0046: Browse's leaf detail view embeds the Sessions history/manage panel

**Status:** Accepted
**Supersedes (in part):** the 2026-07-21 browse-sessions-navigation-and-closure plan §4.1/§4.3,
which had Browse's "Sessions" eye open `/Jobs/Work` as the only place a leaf's session history and
Finish/pause action are shown.

## Context

`/Jobs/Browse`'s leaf detail view already renders a branch's child nodes (the Subtree table) at the
bottom of the page. A leaf never has children, so that slot was empty for a leaf — its Sessions
history was reachable only by following the "Sessions" link to `/Jobs/Work`, a full page navigation
away from the tree the user was just browsing.

`LeafWorkSessionsPanelModel`'s own doc comment already anticipated this: it describes itself as "the
`_LeafWorkSessions` partial shared by `Browse`'s leaf detail view and `Work`" and its
`ExtraHiddenFields` mechanism exists specifically so each host page's own bound properties round-trip
correctly. The wiring into `Browse` was simply never finished — `BrowseModel` never built the panel
model or called `IJobQueries.GetLeafSessionsAsync`, and `BrowseWorkSessionTests` asserted the
opposite ("Browse never finishes a session inline itself").

## Decision

A leaf's detail view on Browse renders the same `_LeafWorkSessions` partial `/Jobs/Work` uses,
in the slot the Subtree table occupies for a branch — the two are mutually exclusive since no node
has both children and leaf work. This gives the panel's existing worker filter, session history, and
per-session Finish/pause + Correct actions directly on Browse, without a page navigation.

`BrowseModel` gains a `Panel` property and the same `GetLeafSessionsAsync`/worker-filter/
`OnPostFinishAsync` machinery `WorkModel` already has, keyed under its own filter-memory key
(`Jobs.Browse.WorkedBy.{leafId}`, distinct from Work's `Jobs.Work.WorkedBy.{leafId}`) so the two
pages' remembered worker choices do not collide.

The Browse toolbar's "Sessions" link to `/Jobs/Work` is **not** removed: `/Jobs/Work` remains the
only surface for the achievement-changing composites (Complete job, Reopen, Cancel, Mark
unsuccessful) that the Sessions panel itself does not expose. Browse's embedded panel covers viewing
history and pausing/correcting a session; changing the leaf's outcome still means following that
link.

## Consequences

- `BrowseWorkSessionTests.The_leaf_toolbar_routes_session_work_to_the_unified_work_page_without_an_embedded_sessions_form`
  asserted the pre-reversal behaviour and is rewritten to assert the panel is now present.
- One extra query (`GetLeafSessionsAsync`) per leaf detail view — no different in shape from the
  active-session/capability batches Browse already issues per page.
- A future change to `_LeafWorkSessions` or `LeafWorkSessionsPanelModel` now affects both hosts
  simultaneously, which is the point: one table, one set of row actions, wherever a leaf's Sessions
  are shown.
