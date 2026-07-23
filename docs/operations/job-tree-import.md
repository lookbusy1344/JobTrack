# Bulk-generating a tree of job nodes from JSON

`JobTrack.AdminCli`'s `import-tree` command reads a flat JSON array of nodes and atomically creates
them as a job-node subtree, all owned by one existing employee — a bulk-authoring tool for small
trees, not a general-purpose migration path. It works against either provider, runs the same way
inside the Docker image (`--entrypoint ./admincli/JobTrack.AdminCli`, see the Dockerfile header), and
either every node and prerequisite edge is created or none is: the whole batch runs in one database
transaction (`IJobCommands.ImportSubtreeAsync`), so a validation failure partway through a large
import leaves nothing behind to clean up by hand.

Run it against a deployed, bootstrapped database — see the README's "Running on a development
server" for those steps and for where the connection strings below come from.

```bash
# PostgreSQL
dotnet run --project src/JobTrack.AdminCli -- import-tree --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_dev" --username <username> --file samples/job-tree-imports/building-a-house.json

# SQLite
dotnet run --project src/JobTrack.AdminCli -- import-tree --provider sqlite --connection-string "Data Source=jobtrack-web-dev.db" --username <username> --file samples/job-tree-imports/building-a-house.json
```

`--username` names the employee every created node is owned by *and* the actor the command runs as
(there is deliberately no separate actor/owner split — see `JobTreeImportCommand`'s own doc comment).
`--parent-id <job-node-id>` anchors the import under an existing node; omit it and the import attaches
under the tree root (`job_node` id `1`).

## The file format

Each row in the JSON file is:

```jsonc
{ "id": 2, "parentId": 1, "title": "Excavate foundations", "prerequisiteIds": [6] }
```

- `id` — a file-local identifier, unique within the file. Never a real `job_node` id.
- `parentId` — another row's `id`, or `null`/omitted to attach directly under `--parent-id`.
- `title` — the new node's description.
- `prerequisiteIds` (optional) — file-local `id`s of other rows in the same file that must succeed
  before this one is ready (spec §6); a node may list more than one. An edge may connect any two
  nodes in the file that are not ancestor/descendant of each other — leaf-leaf, leaf-branch,
  branch-leaf, and branch-branch are all valid, as long as the edge isn't a hierarchy edge in
  disguise.

## Importing work that has already happened

A row may also record work already done against it, so a bulk-authored tree arrives with the history
its author already knows about instead of uniformly untouched. The import attaches `LeafWork`,
records one or more work sessions, and sets the achievement — all inside the same transaction that
creates the nodes, so the "everything or nothing" guarantee still holds.

There are two spellings, and a row uses one or the other, never both:

```jsonc
// Relative: how long before the import each event happened.
{ "id": 3, "title": "First-fix plumbing", "open": "2 days", "closed": "1 day" }

// Absolute: ISO 8601 timestamps, each with an explicit offset.
{ "id": 8, "title": "Fit worktop", "start": "2026-07-16T08:30:00Z", "end": "2026-07-16T16:00:00Z" }
```

- `open` — how long before the import the work started, e.g. `"2 days"`. Accepts
  `minutes`/`hours`/`days`/`weeks` (and the short forms `m`/`h`/`d`/`w`), whole or decimal:
  `"90 minutes"`, `"36 hours"`, `"1.5 days"`, `"3d"`. The import captures the clock once at start, so
  every row in a file counts back from the same instant.
- `closed` (optional) — how long before the import the work finished. Requires `open`. Omit it and
  the leaf is left `InProgress` with an open session — `{ "open": "2 days" }` reads as "started two
  days ago, still going".
- `start` / `end` — the absolute alternative to `open`/`closed`, same open/closed rules. An explicit
  offset (`Z` or `+01:00`) is required rather than assumed, since these instants are compared against
  prerequisite finish times where an hour's drift changes the answer.
- `outcome` (optional) — how a closed leaf ended: `success` (the default), `cancelled`, or
  `unsuccessful`. Only valid on a row that closes; an unfinished job is always in progress.

Only leaves may carry work, and the prerequisite rules (spec §6) are enforced against the recorded
history, not just the end state. An import is rejected, whole, when a row records work but:

- has children in the same file (a branch cannot hold `LeafWork`);
- depends on something that never reaches `Success` in the batch — including a prerequisite left
  open, or a prerequisite branch with any non-succeeding leaf beneath it;
- starts *before* one of its prerequisites finished, which is a chronologically impossible history
  even though replaying it in dependency order would otherwise satisfy the gate;
- closes without ever finishing, finishes before it starts, or is dated in the future.

Prerequisites inherited from ancestors *outside* the file are enforced too — those are rechecked
against real database state inside the import transaction.

## Worked examples

`samples/job-tree-imports/` has seven, roughly from simplest to largest:

| File | Nodes | What it demonstrates |
|---|---|---|
| `experimental-work.json` | 5 | 2 levels; one dependent leaf with two prerequisites. |
| `kitchen-refit-in-progress.json` | 10 | The only example carrying work history: closed leaves, one still open, one `unsuccessful`, one dated absolutely, three not started. |
| `farming-a-field.json` | 16 | 4 levels, mostly a linear branch-dependency chain rather than fan-out. |
| `building-a-house.json` | 17 | 4 levels; every leaf/branch prerequisite-edge combination plus two double-prerequisite nodes. |
| `organising-a-fun-run.json` | 25 | 4 levels; dependencies crossing freely between sibling branches, and one leaf ("Set up start/finish line and timing") decomposed into children of its own. |
| `organising-a-college-election.json` | 30 | 4 levels, the largest: a mostly sequential spine with fan-in (briefing candidates requires both the published candidate list and the approved rules), and a branch — "Verify candidate eligibility", itself two leaves — standing as a prerequisite for a later step. |
| `implementing-ai-enrolment-system.json` | 22 | 4 levels; a complete finished project (every leaf carries absolute `start`/`end` history, all reaching `success`) across three parallel sub-sections — logic engine, MIS write-back, and load testing under 100 simultaneous users — over roughly three working months, every session bounded to weekday 09:00-17:00. Demonstrates costing over a realistically-shaped completed subtree. |
