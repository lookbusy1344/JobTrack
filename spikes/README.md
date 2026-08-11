# De-risking spikes (plan §5.3)

Throwaway proof code for the implementation plan's Phase 0 spikes. Results are written up in
`docs/traceability/spike-report.md`. This code does not pass through the delivery gates (§6.7,
§7.5, §8.7) and is not referenced by production code; `spikes/Directory.Build.props` isolates it
from the repo's strict shared build configuration.

## Reproducing the PostgreSQL spikes (1–4, 7)

Requires a local PostgreSQL 18 instance running and reachable as your OS login role (the
concurrent-test scripts resolve this via `$(whoami)`; the `psql` invocations below do the same
implicitly by omitting `-U`, so no username is embedded here).

```bash
psql -d postgres -c "CREATE DATABASE jobtrack_spike;"

psql -d jobtrack_spike -f sql/01-single-root.sql
./sql/01-single-root-concurrent-test.sh
psql -d jobtrack_spike -f sql/01b-single-root-count-only-counterfactual.sql
./sql/01b-count-only-concurrent-test.sh

psql -d jobtrack_spike -f sql/02-prerequisite-cycle.sql
./sql/02-cycle-concurrent-test.sh

psql -d jobtrack_spike -f sql/03-gist-overlap.sql
./sql/03-gist-overlap-concurrent-test.sh

psql -d jobtrack_spike -f sql/04-advisory-lock-ordering.sql
./sql/04-advisory-lock-ordering-test.sh

psql -d jobtrack_spike -f sql/05-ltree-hierarchy.sql

psql -d postgres -c "DROP DATABASE jobtrack_spike;"
```

## Reproducing the .NET spikes (5–6)

```bash
cd dst-spike && dotnet run
cd ../cost-sweep-spike && dotnet run
```

Both are plain console apps (`net10.0`), pinned to their own package versions independently of the
solution's central package management — see `Directory.Build.props` in this directory.
