# NuGet package metadata gate

**Closes:** Implementation plan §7.5 gate item "NuGet package metadata, symbols, source link,
deterministic output, and dependency vulnerability checks pass."

## What was added

`Directory.Build.props` (applies to all 25 projects; only the 6 packable library projects below
produce a `.nupkg`):

- `PublishRepositoryUrl=true`, `EmbedUntrackedSources=true` — embeds source into the symbol
  package so a debugger can step into library code from the published `.pdb` without a matching
  local checkout, on top of the existing `Deterministic=true`/`ContinuousIntegrationBuild`
  reproducible-build settings.
- `IncludeSymbols=true`, `SymbolPackageFormat=snupkg` — produces a separate portable-PDB symbol
  package alongside every `.nupkg`.
- `Microsoft.SourceLink.GitHub` package reference (`PrivateAssets=all`, matching the existing
  Roslynator/VS-Threading-Analyzers/RecordValueAnalyser analyzer style) — maps embedded/symbol
  source paths back to the GitHub repository at the commit each build was produced from.

`Directory.Packages.props`: added `Microsoft.SourceLink.GitHub` version `10.0.300` under a new
`Packaging` `ItemGroup`.

Each of the 6 packable library projects (`src/JobTrack.Abstractions`, `src/JobTrack.Domain`,
`src/JobTrack.Application`, `src/JobTrack.Persistence.Shared`, `src/JobTrack.Persistence.PostgreSql`,
`src/JobTrack.Persistence.Sqlite`) also gained a `PackageReadmeFile=README.md` property and a short
`README.md` (reusing the project's existing `<Description>` text) to eliminate the NU5039
missing-readme packaging warning — `dotnet pack` reported it before this change on all 6 projects.

## Re-verifying

Pack each of the 6 library projects (Release, output to a scratch directory — do not commit
packed output) and confirm a `.nupkg` and `.snupkg` are produced with no `NU5xxx` warnings:

```sh
for p in JobTrack.Abstractions JobTrack.Domain JobTrack.Application \
         JobTrack.Persistence.Shared JobTrack.Persistence.PostgreSql JobTrack.Persistence.Sqlite; do
  dotnet pack src/$p/$p.csproj -c Release -o /tmp/jobtrack-pack
done
```

Vulnerability check (must report "no vulnerable packages" for every project):

```sh
dotnet list package --vulnerable --include-transitive
```

Last run 2026-07-07: clean across all 25 projects, including after adding the
`Microsoft.SourceLink.GitHub` dependency.

## Out of scope here

Publishing to a NuGet feed — these packages are internal library components (impl plan §7.5 item
5), not a published external product; `dotnet pack` is exercised as a quality gate only.
