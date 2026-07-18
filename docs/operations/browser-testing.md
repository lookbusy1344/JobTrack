# Browser-based end-to-end testing

**Closes:** Fix plan §2.5 (`docs/plans/2026-07-08-fix-plan.md`), plan §8.5/§8.7 responsive,
keyboard, focus, and accessibility evidence.

## Tool choice

`Microsoft.Playwright` drives a real Chromium browser against a real Kestrel-hosted instance of
`JobTrack.Web` (`tests/JobTrack.Web.EndToEndTests/BrowserFixture.cs`). `WebApplicationFactory`'s
in-memory `TestServer` (already used by the rest of the test suite) cannot serve a real browser --
Playwright needs an actual listening socket -- so the fixture instead:

1. deploys a disposable SQLite database and bootstraps an administrator, exactly like the other
   end-to-end tests;
2. picks a free loopback port and writes a short-lived self-signed certificate to a temp file
   (avoids depending on the machine's `dotnet dev-certs https --trust` state, which a CI runner or
   sandbox may never have run);
3. launches the built `JobTrack.Web.dll` as a **real child process** (`dotnet <path>`), passing the
   port, certificate, and database connection string via environment variables, and polls
   `/Account/Login` until it responds;
4. launches headless Chromium via Playwright and drives it against that process.

`Deque.AxeCore.Playwright` (which bundles axe-core, so no separate script fetch happens at test
time) supplements the manual keyboard/focus/reflow checks with an automated accessibility scan, per
fix-plan §2.5: "as a supplement, not the only acceptance evidence."

## One-time setup: installing the browser binary

`dotnet restore`/`dotnet build` only fetches the `Microsoft.Playwright` NuGet package (the .NET
API and driver) -- it does **not** download the browser binary itself. Before
`JobBrowseBrowserTests` can run, from the repository root:

```bash
dotnet build tests/JobTrack.Web.EndToEndTests/JobTrack.Web.EndToEndTests.csproj
pwsh tests/JobTrack.Web.EndToEndTests/bin/Debug/net10.0/playwright.ps1 install chromium firefox webkit
```

Firefox and WebKit are only exercised by `CrossBrowserCompatibilityTests` (plan §8.7 browser
compatibility) -- every other browser-test class uses Chromium.

This is a one-time step per machine (the binary is cached under
`~/Library/Caches/ms-playwright` on macOS, `~/.cache/ms-playwright` on Linux). It requires network
access to Playwright's CDN; re-run it after bumping the `Microsoft.Playwright` package version in
`Directory.Packages.props`, since the driver and browser binary versions must match.

Without this step, `JobBrowseBrowserTests` fails fast with a clear
`Executable doesn't exist at .../headless_shell` `PlaywrightException` naming the exact install
command to run -- it does not hang or time out.

## Viewport matrix

Four representative viewports (`JobBrowseBrowserTests`), matching common device classes rather than
exact device models:

| Class | Width x height |
|---|---|
| Small phone | 375 x 667 |
| Large phone | 414 x 896 |
| Tablet | 768 x 1024 |
| Desktop | 1280 x 800 |

Each is checked for unintended horizontal overflow (`document.documentElement.scrollWidth <=
clientWidth`) on the representative job-browse workflow page (plan §8.5 slice 2).

## 400% zoom / text-resize evidence

Playwright has no notion of browser page zoom (only viewport size), so the plan's "400% zoom or
equivalent text-resize evidence" is satisfied via WCAG 1.4.10 Reflow's automatable equivalent: a
320 CSS-pixel-wide viewport is the standard substitute for "content zoomed to 400% on a
1280px-wide view" -- both require the same reflowed, non-horizontally-scrolling layout. See
`Reflowing_to_a_320px_wide_viewport_keeps_content_and_controls_usable`.

## Coverage today

All ten §8.5 slices have real-browser evidence: sign-in/browse (`JobBrowseBrowserTestsBase`),
create/edit/move/decompose (`JobNodeStructureBrowserTestsBase`), leaf work sessions
(`LeafWorkSessionBrowserTestsBase`), prerequisites/achievement
(`PrerequisitesAchievementBrowserTestsBase`), schedule (`ScheduleBrowserTestsBase`), rate
administration (`RateAdministrationBrowserTestsBase`), cost reports (`CostReportBrowserTestsBase`),
audit browsing (`AuditBrowsingBrowserTestsBase`), and admin account management
(`AdminAccountManagementBrowserTestsBase`). Each follows the same shape: the representative
workflow gets the full viewport matrix, reflow, and keyboard/focus checks; every page in the slice
also gets an automated accessibility scan.

Every one of those classes runs against both `SqliteBrowserFixture` and `PostgreSqlBrowserFixture`
(plan §8.7: "both PostgreSQL and SQLite configurations") via an abstract `*Base` class plus
`Sqlite*`/`PostgreSql*` sealed subclasses -- see `BrowserFixture`'s `Provider` abstraction.
`CrossBrowserCompatibilityTests` separately samples the sign-in/browse workflow under Firefox and
WebKit (plan §8.7 browser compatibility) via `FirefoxBrowserFixture`/`WebKitBrowserFixture`, both
SQLite -- rendering-engine differences are orthogonal to database provider, so this doesn't repeat
the full matrix a third and fourth time.

Accessibility violations found by future runs of this scan should be fixed or explicitly recorded
here with risk acceptance (fix-plan §2.5 acceptance check). No findings have been recorded as of
this writing -- every scan across every slice, provider, and engine combination has passed clean.
