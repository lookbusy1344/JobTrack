# Required .NET global tools

JobTrack's gates depend on a small number of .NET CLI tools that are installed as **global** tools
(`dotnet tool install --global`), not as a per-repo `.config/dotnet-tools.json` local manifest. This
is a deliberate, standing convention for this working copy — the monorepo's development tooling is
managed once per machine rather than restored per repository — so there is intentionally **no**
`.config/dotnet-tools.json` in JobTrack.

Because the versions are therefore not pinned by a checked-in manifest, they are recorded here.
Reproduce the toolset with the pinned versions below (`--version` is exact, not floating):

| Tool | Package id | Pinned version | Used by |
|------|-----------|---------------|---------|
| Stryker.NET | `dotnet-stryker` | `4.16.0` | Mutation-testing gate (§7.5 item 4) — see [mutation-testing-gate.md](mutation-testing-gate.md). Required now. |
| LibMan CLI | `Microsoft.Web.LibraryManager.Cli` | `3.0.114` | Restoring the pinned Bootstrap client assets in Phase 3 (§8.1). Not yet used in Phases 1–2. |

```sh
dotnet tool install --global dotnet-stryker --version 4.16.0
dotnet tool install --global Microsoft.Web.LibraryManager.Cli --version 3.0.114
```

Confirm what is installed with `dotnet tool list --global`.

## Why global rather than a local manifest

- These are **development/verification** tools, not build-time dependencies of any project in the
  solution. The commit gate (`dotnet build` / `format` / `test`) needs none of them; they back the
  additional gates (mutation testing now, client-asset restore in Phase 3) that are run
  deliberately, not on every build.
- A global install matches how the rest of the monorepo's tooling is managed on this machine and
  avoids a per-repo restore step for tools that rarely change.
- The trade-off is that the version is not enforced by tooling. This table is the record of record;
  if a gate result depends on a tool version (the mutation score does — a different Stryker release
  can change which mutants are generated), update the pinned version here in the same change that
  adopts it, exactly as the mutation-gate doc already treats its recorded score.

If JobTrack is ever extracted from this monorepo or built on a machine without these tools
pre-installed (e.g. a clean CI runner), reintroduce a `.config/dotnet-tools.json` pinning the same
versions and restore it with `dotnet tool restore`; that is a packaging concern for that
environment, not a change to this convention.
