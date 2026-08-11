# Third-party notices

JobTrack is licensed under the GNU Affero General Public License v3.0
(`LICENSE`). It incorporates or depends on the third-party components below,
each under its own license. Nothing in `LICENSE` alters those terms.

**Every license listed here is AGPLv3-compatible.** No component prevents
JobTrack from being distributed under the AGPL, and none imposes a conflicting
obligation.

## Redistributed with the application

These ship inside the deployed application, and so form part of the
"Corresponding Source" obligation under AGPLv3 §1 and §13.

### Vendored web assets (`src/JobTrack.Web/wwwroot/lib/`)

| Component | Version | License | AGPLv3 compatibility |
| --- | --- | --- | --- |
| Bootstrap | 5.3.8 | MIT | Compatible |
| Mulish (Fontsource) | 5.2.8 | SIL Open Font License 1.1 | Compatible (aggregation) |

Bootstrap: Copyright (c) 2011-2025 The Bootstrap Authors. Licensed under MIT
(<https://github.com/twbs/bootstrap/blob/main/LICENSE>).

Mulish: Copyright (c) 2019 The Mulish Project Authors
(<https://github.com/googlefonts/mulish>). Licensed under the SIL Open Font
License, Version 1.1 (<https://openfontlicense.org>). The font is bundled
alongside the application rather than combined into it — mere aggregation under
AGPLv3 §5 — so the OFL and the AGPL do not interact. The OFL requires this
notice to travel with the font files, and forbids selling the font by itself.

### NuGet packages

| Package | Version | License |
| --- | --- | --- |
| Microsoft.AspNetCore.DataProtection | 10.0.10 | MIT |
| Microsoft.AspNetCore.DataProtection.EntityFrameworkCore | 10.0.10 | MIT |
| Microsoft.AspNetCore.OpenApi | 10.0.10 | MIT |
| Microsoft.Data.Sqlite | 10.0.10 | MIT |
| Microsoft.EntityFrameworkCore | 10.0.10 | MIT |
| Microsoft.EntityFrameworkCore.Relational | 10.0.10 | MIT |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.10 | MIT |
| Microsoft.Extensions.Identity.Core | 10.0.10 | MIT |
| Microsoft.OpenApi | 2.10.0 | MIT |
| NodaTime | 3.3.3 | Apache-2.0 |
| Npgsql | 10.0.3 | PostgreSQL |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL |
| Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime | 10.0.3 | PostgreSQL |
| QRCoder | 1.8.0 | MIT |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0 |

MIT, BSD-3-Clause and the PostgreSQL License are permissive and GPL-compatible.
Apache-2.0 is compatible with GPLv3 and AGPLv3 specifically — the compatibility
is one-way, so Apache-2.0 code may be incorporated into an AGPLv3 work but not
the reverse. Apache-2.0 also requires that this notice file accompany
redistribution and that modified files be marked as changed.

## Build-time only

Analyzers and source-link tooling. Not redistributed, and not part of
Corresponding Source.

| Package | Version | License |
| --- | --- | --- |
| lookbusy1344.RecordValueAnalyser | 1.3.1 | MIT |
| Microsoft.CodeAnalysis.PublicApiAnalyzers | 5.6.0 | MIT |
| Microsoft.SourceLink.GitHub | 10.0.301 | MIT |
| Microsoft.VisualStudio.Threading.Analyzers | 18.7.23 | MIT |
| Roslynator.Analyzers | 4.15.0 | Apache-2.0 |

## Test-time only

Not redistributed.

| Package | Version | License |
| --- | --- | --- |
| AwesomeAssertions | 9.5.0 | Apache-2.0 |
| coverlet.collector | 10.0.1 | MIT |
| Deque.AxeCore.Playwright | 4.12.0 | MIT (bundles axe-core, MPL-2.0) |
| FsCheck | 3.3.4 | BSD-3-Clause |
| FsCheck.Xunit | 3.3.4 | BSD-3-Clause |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.10 | MIT |
| Microsoft.CodeAnalysis.CSharp | 5.6.0 | MIT |
| Microsoft.NET.Test.Sdk | 18.8.1 | MIT |
| Microsoft.Playwright | 1.61.0 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |

`Deque.AxeCore.Playwright` embeds axe-core, which is MPL-2.0. MPL-2.0 is
GPL-compatible via its "Secondary Licenses" mechanism, and is file-level
copyleft reaching only modified MPL files. The package is test-only and is not
redistributed, so nothing attaches to JobTrack either way.

## The `.NET` runtime and ASP.NET Core shared framework

Not vendored here. Microsoft distributes them under MIT, and they are consumed
as a platform rather than incorporated, so they fall under AGPLv3 §1's "System
Libraries" exception and need not be included in Corresponding Source.

## Regenerating

Package versions come from `Directory.Packages.props`; vendored web assets from
`src/JobTrack.Web/libman.json`. Update this file when either changes.
