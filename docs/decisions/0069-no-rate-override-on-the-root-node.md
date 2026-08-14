# ADR 0069: No rate override on the root node

**Status:** Accepted
**Builds on:** ADR 0015 (permanent, un-re-parentable root), ADR 0035 (root derived from
`parent_id IS NULL`), ADR 0064 (provider-neutral port bodies). **Refines:** spec §9.2 (job-specific
rate overrides).

## Context

`node_rate_override` is keyed on `(node_id, user_id)`, both `NOT NULL` (schema 0011). Nothing stopped
an override row from targeting the root. Because an override applies to its node *and every
descendant* (spec §9.2), a root override applies to the worker's entire tree — it outranks the
worker's `user_cost_rate` (rate precedence §9.3 level 2 over level 3) across everything.

That is not a per-node deviation. It is a restatement of the worker's own tree-wide rate, which the
`user_cost_rate` table (level 3) and `app_user.default_hourly_rate` (level 4) already express. A root
override is therefore either redundant with, or a shadow of, the two levels below it — and it invites
the reader to treat "the tree" as a costable node in its own right, which it is not: the root is a
structural container whose cost is the sum of all descendant work (§7 line 546).

The scenario people reach for — "price this whole tree specially for this worker" — has no meaning at
the root, because there is exactly one root (§4.2 invariant 1). If per-tree pricing across *several*
project trees were ever wanted, that is a distinct concept (a per-tree or per-client rate), not a
node override, and would get its own decision.

The one genuine capability lost is a per-node override on a **single-node tree** (a node that is both
Root and Leaf under ADR 0035). That override would be indistinguishable from the worker's own rate on
the only node that exists, and a one-leaf installation is not an operational scenario. Not worth
preserving.

## Decision

A `node_rate_override` shall never target the root node (`node_id` referencing a node with
`parent_id IS NULL`). This is a hierarchy invariant (spec §4.2 invariant 11), stated generically so
any future per-node override table inherits the same rule.

Enforcement is **insert/update-only** on the override, in three coordinated places (the FDG
database→library layering):

1. **PostgreSQL** — a `BEFORE INSERT OR UPDATE` trigger on `node_rate_override` (new schema version;
   0011 is deployed and never edited in place, per ADR 0011) rejecting a root `node_id`.
2. **SQLite** — the equivalent `AFTER INSERT`/`AFTER UPDATE` `RAISE(ABORT, …)` triggers, edited into
   schema 0011 in place (no deployed SQLite instance).
3. **Library** — `RateCommandPort` (the provider-neutral shared body, ADR 0064) rejects a root node
   in the same write transaction as `AddNodeRateOverrideAsync`/`CorrectNodeRateOverrideAsync`,
   surfacing an `InvariantViolationException` (`node-rate-override-on-root`) rather than a raw driver
   error.

No guard is needed on the hierarchy-move path: the single-root partial unique index plus ADR 0015's
permanent-root guard make it impossible for an override-bearing node to become the root, or for the
root to stop being the root. Insert/update coverage on the override is complete.

## Consequences

- **Schema:** one new PostgreSQL schema version adds the trigger; SQLite 0011 gains the matching
  triggers in place.
- **Library:** `RateCommandPort.EnsureNodeExistsAsync` tightens to also reject the root; both the add
  and correct paths already funnel through it.
- **Tests:** the schema-0011 contract tests seeded their overlap/adjacency fixtures on the root only
  incidentally; they move to a child node (subject unchanged) and gain a root-rejection case at the
  contract, provider, and port-contract layers.
- **No data migration:** the deployed PostgreSQL instance holds no root overrides (the root has no
  worker-specific rate use), so the new trigger has nothing to reject retroactively; the next
  migration job applies it forward-only.
- **Docs:** spec §4.2/§9.2/§17.2, `docs/database-entities.md`, and `docs/rate-resolution.md` describe
  the root as a structurally special, override-free node.
