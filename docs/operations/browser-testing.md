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

The **client-side assets** are the other one-time setup this suite depends on, and the one whose
absence is easiest to misread. Bootstrap and the Mulish display face are pinned in
`src/JobTrack.Web/libman.json` and restored into the git-ignored `wwwroot/lib/` (`cd
src/JobTrack.Web && libman restore` -- see the developer guide's "Client-side assets"). `dotnet build` does not
restore them. Without them the host still serves every page, so the failures arrive as a pile of axe
`color-contrast` violations and layout assertions -- unstyled text on an unstyled background, scanned
faithfully -- rather than anything naming a missing stylesheet.

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

## Passkeys (ADR 0071 Stage 7)

`PasskeyBrowserTests` exercises the native ASP.NET Core WebAuthn ceremonies end-to-end using
Chromium's CDP virtual authenticator (`PasskeyVirtualAuthenticator`): a software authenticator that
satisfies `navigator.credentials.create`/`.get` without hardware or a user prompt, so the real
protocol path -- attestation, discoverable-credential storage, assertion, RP ID/origin validation --
runs against the real Kestrel HTTPS instance. Handler substitutes cover the error branches in the
integration suite; only a virtual authenticator is protocol evidence.

The passkey fixtures (`PasskeySqliteBrowserFixture`/`PasskeyPostgreSqlBrowserFixture`) run the app
with `Authentication:Passkeys:Enabled=true`, bound to the host name **`localhost`** rather than the
`127.0.0.1` the other fixtures use -- WebAuthn forbids a bare IP address as an RP ID. The RP ID is
`localhost` and the single allowed origin is the exact `https://localhost:<port>` the browser
navigates to.

Covered, on both providers:

- enrol a discoverable credential from `/Account/Security` and see it listed;
- username-less discoverable sign-in from `/Account/Login`;
- replay rejection for a previously successful non-zero-counter assertion;
- sign-in with no TOTP step even when TOTP is enabled (ADR 0071 §1.3 assurance table);
- remove a passkey, leaving the account with none;
- `/Account/Security` with a passkey listed has no critical/serious axe violations;
- the login enhancement runs under a `script-src 'self'` CSP with no inline script; and
- the password form still signs in with JavaScript disabled (the enhancement is inert, not required).
- the explicit passkey button surfaces option-generation failures instead of silently doing nothing.

The PostgreSQL two-host acceptance suite separately proves that passkey assertion state generated by
one application host is accepted by another host sharing the Data Protection key repository.

Chromium only: the CDP WebAuthn virtual authenticator has no Firefox/WebKit equivalent.

The Security axe scan first flagged the row's **Rename** button (`btn-outline-secondary`: Bootstrap
compiles its colour to the literal `#6c757d`, only 3.99:1 on the Console page ground `#f1ece5`).
`site.css` now re-skins `.btn-outline-secondary` onto the Console ink/line tokens, matching
`.btn-secondary`, which also fixes the same button on `/Audit` and `/Jobs/AwaitingProgress`.

### Manual synthetic-account device matrix

The virtual authenticator proves the protocol; real synced/roaming authenticators are a release
check, not CI (ADR 0071 §10). Record each with exact OS/browser/authenticator versions, using only
synthetic accounts and keeping real PII out of screenshots:

| Platform / authenticator | Create | Use (sign-in) | Remove | Notes |
|---|---|---|---|---|
| macOS Safari + iCloud Keychain | | | | synced platform |
| iPhone/iPad Safari + iCloud Keychain | | | | synced use + cross-device QR |
| Windows Edge/Chrome + Windows Hello | | | | platform |
| Non-platform Windows passkey manager | | | | plugin provider |
| FIDO2 YubiKey (PIN + discoverable), USB | | | | roaming |
| FIDO2 YubiKey over NFC (mobile) | | | | roaming |
| TOTP-enabled account | n/a | | n/a | password requires TOTP; passkey does not |
