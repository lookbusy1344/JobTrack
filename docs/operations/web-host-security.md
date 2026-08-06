# Web host security configuration

**Closes:** Fix plan §2.4 (`docs/plans/2026-07-08-fix-plan.md`), documenting the settings plan
§8.2 calls for that a `WebApplicationFactory`-based integration test cannot meaningfully prove,
plus the required host-level configuration and filesystem permissions for a real deployment.

## Required configuration outside Development

`src/JobTrack.Web/Program.cs` fails startup closed (throws `InvalidOperationException` before the
host is built) when any of these is left unconfigured and `ASPNETCORE_ENVIRONMENT` is not
`Development`:

- `ForwardedHeaders:KnownProxies` (a JSON array of IP addresses) and/or
  `ForwardedHeaders:KnownNetworks` (a JSON array of CIDR ranges, e.g. `"10.0.0.0/24"`) — at least
  one entry across the two is required. This is the reverse proxy's own address (spec §9.1: Kestrel
  binds to a private loopback endpoint or local socket behind a locally managed reverse proxy that
  terminates HTTPS), not a wildcard or the deployment's public address range.
- `DataProtection:KeyPath` — an absolute filesystem path outside the application's deployment
  directory (so a redeploy that replaces the app directory doesn't discard the key ring, and so an
  application-level path-traversal bug can't reach it through a relative path).
- `DataProtection:CertificatePath` — an absolute path to a PKCS#12 certificate containing its private
  key. ASP.NET Core uses it to encrypt each persisted data-protection XML key.
- `DataProtection:CertificatePasswordPath` — an absolute path to a protected file containing the
  PKCS#12 password. Keep the password out of environment variables and process arguments.
- `AllowedHosts` — a `;`-separated list of the host names this deployment answers to, e.g.
  `jobtrack.example.com` or `jobtrack.example.com;www.jobtrack.example.com`. ASP.NET Core's own
  default (unset, or `*`) disables host filtering entirely, which lets a request forge the absolute
  URLs the application generates from the `Host` header and defeats virtual-host isolation on a
  shared front end, so both are rejected here. A subdomain wildcard (`*.example.com`) is still
  accepted — only the bare catch-all is not. `appsettings.json` ships this empty deliberately; there
  is no correct default for someone else's hostname.

`ProductionSecurityConfigurationTests` (`tests/JobTrack.Web.IntegrationTests/`) proves each guard:
`Startup_fails_closed_outside_development_without_forwarded_header_configuration` covers the
forwarded-headers one; `..._without_a_data_protection_key_path` covers data protection, isolated by
configuring a trusted proxy so only that guard is exercised;
`..._without_a_data_protection_certificate` covers application-level encryption of persisted keys;
`Startup_fails_closed_outside_development_with_wildcard_allowed_hosts` covers host filtering, with
both earlier guards satisfied. `Startup_succeeds_outside_development_when_allowed_hosts_names_a_real_host`
is the positive control — a fully configured Production host starts, so the four failure tests are
known to trip on the setting under test rather than on the environment being Production at all.

## Required filesystem permissions for the data-protection key path

The directory named by `DataProtection:KeyPath` must:

- exist before the application starts (the framework does not create parent directories);
- be writable by the unprivileged service account the application runs as (spec §9.1: "a dedicated
  unprivileged operating-system service");
- be readable/writable only by that account and any operator account performing key-rotation
  backups — key material here decrypts every Identity authentication cookie and CSRF token
  currently in flight, so exposure has the same blast radius as a leaked session-signing secret;
  and
- be included in the backup/restore runbook (`docs/operations/postgresql-backup-restore.md`
  documents the database side; the key path is a separate, non-database artifact that must be
  backed up in step with it, or every existing session and antiforgery token invalidates on
  restore).

The certificate and password files must be readable only by the service identity and operators who
rotate or restore the key ring. Back up the certificate together with the key ring: replacing or
losing its private key makes existing XML keys, cookies, antiforgery tokens, and protected TOTP
secrets unreadable. Do not overwrite the pair in place: the current single-certificate configuration
does not constitute a rotation mechanism. A future rotation must retain the old private certificate
as an additional decrypting certificate until every key encrypted by it has expired.

## Real-Kestrel evidence (security review remediation §2.6)

`tests/JobTrack.Web.EndToEndTests/ProductionSecuritySmokeTests.cs` boots the real `JobTrack.Web`
process (not `WebApplicationFactory`'s in-process `TestServer`, which bypasses Kestrel's
socket-level request pipeline entirely) in the `Production` environment against real Kestrel
sockets, via `ProductionHostFixture`. It proves:

- **Oversized body rejection without relying on `Content-Length`.** A chunked request (no declared
  `Content-Length`) exceeding `KestrelServerOptions.Limits.MaxRequestBodySize` (`Program.cs`,
  `MaxRequestBodyBytes`) is rejected with `400 Bad Request` — the real Kestrel behavior for a body
  that exceeds the limit mid-read, distinct from the `Content-Length`-aware middleware check
  (`SecurityHeadersTests`), which returns `413` when the limit is known upfront.
- **Slow/stalled body rejection.** `KestrelServerOptions.Limits.MinRequestBodyDataRate`
  (`MinRequestBodyDataRateBytesPerSecond`/`MinRequestBodyDataRateGracePeriodSeconds`, `Program.cs`)
  is now configured explicitly rather than left to Kestrel's implicit default. This was added
  because `AddRequestTimeouts`'s `RequestTimeoutSeconds` alone does **not** reliably cut off a
  request whose body trickles in slowly: Razor Pages' built-in form model binding does not
  consistently observe `HttpContext.RequestAborted` while awaiting body bytes, so the app-level
  timeout can fail to terminate a stalled connection. The Kestrel-level data-rate guard operates
  independently of that cooperation and reliably aborts a sub-rate connection shortly after its
  grace period.
  - Proving this took a specific test-content shape: a fixed client-side `await Task.Delay(...)`
    before a second write is not valid evidence, because if the server aborts early, the write
    after the delay can only observe that abort once the delay itself elapses — the measured
    duration would equal the delay regardless of server behavior. The passing test instead trickles
    one byte per second and lets a write fail as soon as the server actually closes the connection.
- **Forwarded-proto trust boundary.** A plain-HTTP request with a spoofed `X-Forwarded-Proto: https`
  header from a *trusted* configured proxy address is honored (no HTTPS redirect); the identical
  request from an *untrusted* address is ignored, and the plain-HTTP request is redirected to HTTPS
  as normal.
- **HSTS.** An HTTPS response includes `Strict-Transport-Security` outside Development. (`HstsMiddleware`'s
  default `ExcludedHosts` skips `localhost`/`127.0.0.1`/`::1` — a deliberate ASP.NET Core convention,
  not a gap — so the test overrides only the request's `Host` header, not the socket address it
  connects to, to observe the header on a production-like hostname.)
