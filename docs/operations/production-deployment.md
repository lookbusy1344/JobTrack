# Production deployment (Linux and Windows Server)

**Closes (in part):** Implementation plan §9.1's "provisioning" and general hosting portion of the
operations runbooks. This document is the host-level runbook: OS service setup, reverse proxy
placement, and PostgreSQL provisioning for the self-hosted single-server topology of
[ADR 0014](../decisions/0014-single-server-deployment.md). The *production* deployment is the Cloud
Run + Cloud SQL topology of
[ADR 0062](../decisions/0062-cloud-run-cloud-sql-production-topology.md), whose runbook is
[`postgresql-cloud-run-deployment.md`](postgresql-cloud-run-deployment.md); this document remains
the reference for a self-hosted installation. It complements, and does not repeat:

- [`web-host-security.md`](web-host-security.md) — the application's own fail-closed configuration
  (`ForwardedHeaders:*`, `DataProtection:KeyPath`, `AllowedHosts`) and the Kestrel-level request
  limits it enforces.
- [`postgresql-backup-restore.md`](postgresql-backup-restore.md) — what the backup/restore smoke
  test proves and the restore procedure.
- [`sqlite-limitations-and-configuration.md`](sqlite-limitations-and-configuration.md) — if SQLite
  is the chosen backend instead of PostgreSQL (the two are mutually exclusive per deployment, not a
  failover pair).

**Not yet covered here, and not yet implemented in the codebase as of this writing** — do not treat
their absence from `Program.cs` as an oversight to route around: OpenTelemetry traces/metrics/logs
(plan §9.2) and a native Windows/systemd service host integration (`UseWindowsService()` /
`Microsoft.Extensions.Hosting.Systemd`). Both are Phase 4 (§9) work; this runbook assumes the
process is supervised by the OS service manager directly (systemd unit, or a Windows Service
wrapper such as NSSM, or IIS's own process activation) rather than by the app opting into the
service-lifecycle APIs itself.

## Topology recap (ADR 0014)

One modest server. The ASP.NET Core application runs as a dedicated unprivileged OS service behind
a locally managed reverse proxy that terminates HTTPS; Kestrel binds to a private loopback endpoint
or local socket only, never a public interface directly. PostgreSQL runs on the same server or a
directly managed database host. No containers, orchestration, multi-node coordination, or
distributed cache — those are deferred until a measured requirement justifies them, not designed
for speculatively.

## Common prerequisites (either OS)

- **Secrets** (database credentials, data-protection key material's backing store if applicable,
  any external service credentials) come from an external secret store appropriate to the host
  environment — OS-protected configuration, a secrets manager, or an environment value injected at
  service start. Never in deployment scripts or configuration files committed to source control.
- **CLI secret channels.** `JobTrack.Database` and `JobTrack.AdminCli` reject a direct
  `--connection-string` containing `Password`/`Pwd`; supply a protected
  `--connection-string-file`, PostgreSQL passfile, or integrated identity instead. Administrator
  bootstrap and employee creation reject plaintext `--password`; use the masked prompt or pipe one
  line to `--password-stdin`. Restrict any secret file to the invoking service account and remove a
  one-use file after the command completes.
- **Data protection** requires an absolute `DataProtection:KeyPath` outside the deployment directory,
  plus absolute `DataProtection:CertificatePath` and `DataProtection:CertificatePasswordPath` files.
  Create them ahead of time and restrict all three to the service account (see
  `web-host-security.md`).
- **`ForwardedHeaders:KnownProxies` / `KnownNetworks`** must list the reverse proxy's own address —
  the loopback address it connects from, not a public range.
- **`AllowedHosts`** must list this deployment's own host names, `;`-separated. Startup rejects an
  unset value and the `*` catch-all outside Development (see `web-host-security.md`).
- **PostgreSQL login role.** The repository ships group roles only
  (`database/postgresql/roles/jobtrack-roles-and-grants.sql`: `jobtrack_owner`,
  `jobtrack_schema_deployer`, `jobtrack_domain`, `jobtrack_identity`, `jobtrack_pat_management`,
  `jobtrack_pat_authentication`, `jobtrack_readonly`, `jobtrack_emergency_reset`) and holds no environment credentials. Create an actual `LOGIN` role
  per environment and grant it membership in the appropriate group role (security review
  remediation §2.6 split the former single `jobtrack_application` role by capability):
  - `jobtrack_domain` for `ConnectionStrings:JobTrackDomain` — `JobTrack.Web`'s `IJobTrackClient`
    connection for domain data and audit writes, and also the connection `JobTrack.AdminCli` and `JobTrack.Database` use for their
    single `--connection-string`: `jobtrack_domain` is a strict superset of what those tools need
    (it retains `identity_user` access alongside its domain grants — see the roles script's own
    documented residual), so provisioning the CLIs' one connection string with this role covers
    every command they run. Do **not** provision the CLIs with `jobtrack_identity` — it cannot reach
    domain tables those commands touch.
  - `jobtrack_identity` for `ConnectionStrings:JobTrackIdentity` — `JobTrack.Web`'s ASP.NET Core
    Identity sign-in path only (password/TOTP verification, security-stamp validation on every
    request). It can read role assignments for claims but cannot create or remove them.
  - `jobtrack_pat_management` for `ConnectionStrings:JobTrackPatManagement` — self-service/admin
    PAT issue/list/revoke operations and their audit rows.
  - `jobtrack_pat_authentication` for `ConnectionStrings:JobTrackPatAuthentication` — bearer-token
    authentication only. It can execute only `pat_try_authenticate` and has no table grants.
  - `jobtrack_schema_deployer` only for the deploy step, and `jobtrack_readonly` for
    reporting/auditor access. Never run the application itself as `jobtrack_owner` or a superuser.

## Linux

### Application

1. **Publish** a self-contained or framework-dependent build (framework-dependent needs the
   matching ASP.NET Core runtime installed separately):

   ```bash
   dotnet publish src/JobTrack.Web -c Release -o /opt/jobtrack/app
   ```

2. **Create a dedicated unprivileged service account** with no login shell and no home directory:

   ```bash
   sudo useradd --system --no-create-home --shell /usr/sbin/nologin jobtrack
   sudo mkdir -p /var/lib/jobtrack/dataprotection-keys
   sudo chown -R jobtrack:jobtrack /opt/jobtrack /var/lib/jobtrack
   sudo chmod 700 /var/lib/jobtrack/dataprotection-keys
   ```

3. **Bind Kestrel to loopback or a local Unix socket only** — never a public interface. Either
   `ASPNETCORE_URLS=http://127.0.0.1:5000`, or a Unix socket via
   `Kestrel:Endpoints:Http:Url=http://unix:/run/jobtrack/jobtrack.sock` in configuration (a socket
   avoids a TCP port entirely and lets filesystem permissions restrict who can connect).

4. **Run it under systemd**, restarted automatically and started at boot:

   ```ini
   # /etc/systemd/system/jobtrack.service
   [Unit]
   Description=JobTrack web application
   After=network.target postgresql.service

   [Service]
   Type=simple
   User=jobtrack
   Group=jobtrack
   WorkingDirectory=/opt/jobtrack/app
   ExecStart=/usr/bin/dotnet /opt/jobtrack/app/JobTrack.Web.dll
   Restart=on-failure
   RestartSec=5
   Environment=ASPNETCORE_ENVIRONMENT=Production
   Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
   Environment=DataProtection__KeyPath=/var/lib/jobtrack/dataprotection-keys
   Environment=DataProtection__CertificatePath=/etc/jobtrack/data-protection.pfx
   Environment=DataProtection__CertificatePasswordPath=/etc/jobtrack/data-protection-password
   Environment=ForwardedHeaders__KnownProxies__0=127.0.0.1
   Environment=AllowedHosts=jobtrack.example.com
   EnvironmentFile=/etc/jobtrack/secrets.env
   NoNewPrivileges=true
   ProtectSystem=strict
   ReadWritePaths=/var/lib/jobtrack

   [Install]
   WantedBy=multi-user.target
   ```

   `/etc/jobtrack/secrets.env` (holding `ConnectionStrings__JobTrackIdentity=...` and, for
   PostgreSQL, `ConnectionStrings__JobTrackDomain=...`, `ConnectionStrings__JobTrackPatManagement=...`,
   and `ConnectionStrings__JobTrackPatAuthentication=...` too — see the role split above) should be
   `chmod 600`, owned by `jobtrack:jobtrack`, and
   excluded from any config management repo that isn't itself a secret store. Enable and start with
   `sudo systemctl enable --now jobtrack.service`.

5. **Reverse proxy** (nginx shown; Caddy is a reasonable simpler alternative) terminates HTTPS and
   forwards to the loopback address, setting the headers the application trusts because
   `ForwardedHeaders:KnownProxies` names this proxy's own address:

   ```nginx
   server {
       listen 443 ssl;
       server_name jobtrack.example.internal;

       ssl_certificate     /etc/ssl/jobtrack/fullchain.pem;
       ssl_certificate_key /etc/ssl/jobtrack/privkey.pem;

       location / {
           proxy_pass http://127.0.0.1:5000;
           proxy_set_header Host $host;
           proxy_set_header X-Forwarded-For $remote_addr;
           proxy_set_header X-Forwarded-Proto $scheme;
       }
   }
   ```

6. **Firewall** — only the reverse proxy's public port (443) is open; the application's loopback
   port/socket is never exposed. `ufw allow 443/tcp` (or the `firewalld` equivalent) is normally
   sufficient once the default policy denies inbound.

### PostgreSQL

1. **Install** via the distribution's package or the PostgreSQL project's own apt/yum repository
   for a current major version (this project's local development instance runs `postgresql@18` via
   Homebrew on macOS; use the equivalent current major version's native package on the target Linux
   distribution).
2. **Bind to a private interface or Unix socket** — `listen_addresses` in `postgresql.conf` should
   name only `localhost` (or be left to the Unix socket alone) when the application runs on the same
   host; a directly managed separate database host instead binds to a private network address the
   application server can reach, never a public one. Scope `pg_hba.conf` entries narrowly (specific
   role, database, and address/socket), not `0.0.0.0/0` or `trust`.

   **A remote (non-loopback, non-Unix-socket) connection string must set `SSL Mode=VerifyFull`
   plus `Root Certificate=<path-to-trusted-CA>`** (security review remediation §2.9). `VerifyCA`
   is insufficient because it authenticates the issuing CA without verifying that the certificate
   belongs to the configured database hostname:

   ```
   Host=db.internal.example;Port=5432;Database=jobtrack;SSL Mode=VerifyFull;Root Certificate=/etc/jobtrack/pg-ca.pem
   ```

   Npgsql's own default, `SSL Mode=Prefer`, neither guarantees encryption nor authenticates the
   server; `Trust Server Certificate=true` accepts any certificate and is not an acceptable
   substitute here. `JobTrack.Web`, `JobTrack.AdminCli`, and `JobTrack.Database` all reject a
   remote connection string that doesn't meet this outside Development/unconditionally
   respectively — see `PostgreSqlTransportSecurity.Validate`. A same-host Unix-domain socket
   (`Host=/tmp;...`, this repository's own local-development shape) or loopback TCP connection is
   exempt, since that traffic never leaves the host.
3. **Create the login role and database**, then deploy schema and roles as described in the
   the developer guide's "Running on a development server → PostgreSQL" section, against this server instead of
   a local one:

   ```sql
   CREATE ROLE jobtrack_domain_login LOGIN PASSWORD '...' IN ROLE jobtrack_domain;
   CREATE ROLE jobtrack_identity_login LOGIN PASSWORD '...' IN ROLE jobtrack_identity;
   CREATE DATABASE jobtrack
       OWNER jobtrack_domain_login
       LOCALE_PROVIDER icu
       ICU_LOCALE 'en-GB'
       TEMPLATE template0;
   ```

   (Provision `jobtrack_owner`/`jobtrack_schema_deployer` login roles the same way for the one-time
   schema deployment step, per the common-prerequisites note above.)
4. **Baseline tuning** — start from the values `pg_tune`-style guidance or the PostgreSQL
   documentation's "Tuning Your PostgreSQL Server" recommends for the host's actual RAM/CPU/disk
   (`shared_buffers`, `effective_cache_size`, `work_mem`, `maintenance_work_mem`, `max_connections`,
   `wal_level`, `max_wal_size`) rather than the installer's defaults, which target a shared/small
   host. Re-tune after the performance budgets in
   [`../traceability/performance-budgets.md`](../traceability/performance-budgets.md) are measured
   against this hardware, not before.
5. **Continuous backup**, complementing the schema-level smoke test in
   `postgresql-backup-restore.md`: WAL archiving plus a base-backup tool (`pg_basebackup`,
   `pgBackRest`, or `WAL-G`) gives point-in-time recovery; the specific backup interval and RPO/RTO
   are set in the runbook per ADR 0014, not hardcoded here. Encrypt backups at rest.
6. **Routine maintenance** — autovacuum is on by default; monitor `pg_stat_user_tables` for
   bloat/dead-tuple counts on the highest-write tables (work sessions, audit events) rather than
   disabling or hand-tuning autovacuum preemptively. Include `job_node` in that monitoring even
   though it is not among the highest-write tables: the Awaiting Progress candidate query is
   index-only-scan-bound on `job_node_parent_id_idx`, so a stale visibility map turns thousands of
   index-only probes into heap fetches. Measured on a 193,570-node fixture, that is the difference
   between ~34 ms and ~64 ms for one page load — see `docs/traceability/performance-budgets.md` §2.2.

## Windows Server

### Application

1. **Install the ASP.NET Core Hosting Bundle** (matching the .NET 10 runtime) on the server — this
   installs both the runtime and the IIS "ASP.NET Core Module v2" (ANCM).
2. **Publish**:

   ```powershell
   dotnet publish src/JobTrack.Web -c Release -o C:\inetpub\jobtrack
   ```

3. **Create a dedicated low-privilege account** to run the application pool — a Group Managed
   Service Account (gMSA) if the server is domain-joined, otherwise a local service account created
   for this purpose only. Restrict NTFS permissions on the deployment directory and the
   data-protection key directory (e.g. `C:\ProgramData\JobTrack\dataprotection-keys`) to that
   account plus the operators who perform key-rotation backups — no broader group.
4. **Host under IIS as the reverse proxy**, with Kestrel running out-of-process behind ANCM:
   - Create an Application Pool with **.NET CLR version: No Managed Code** (ANCM manages the
     out-of-process worker itself) and set its identity to the dedicated account above.
   - Create a site bound to `https://` with a certificate bound in IIS; `web.config` (generated by
     `dotnet publish`) configures ANCM to forward to Kestrel, which by default binds to a
     `localhost`-only port ANCM assigns — the application itself never listens on a public
     interface, matching the same loopback-only rule as the Linux setup.
   - Set `ForwardedHeaders:KnownProxies` to `127.0.0.1` (IIS forwards from the loopback interface to
     the out-of-process worker) via `appsettings.Production.json`, environment variables on the
     Application Pool, or `web.config`'s `<environmentVariables>`.
   - Set `AllowedHosts` to the site's own host name(s) via the same mechanism — the IIS binding does
     not substitute for it, since ANCM forwards the client's original `Host` header through.
   - Set `DataProtection:KeyPath` to the restricted directory from step 3, and all four
     `ConnectionStrings:JobTrackIdentity` (the `jobtrack_identity` role) and
     `ConnectionStrings:JobTrackDomain` (the `jobtrack_domain` role),
     `ConnectionStrings:JobTrackPatManagement` (the `jobtrack_pat_management` role), and
     `ConnectionStrings:JobTrackPatAuthentication` (the `jobtrack_pat_authentication` role) via a protected mechanism (see
     "secrets" below) rather than a plaintext `appsettings.Production.json` committed anywhere.
5. **Alternative to IIS**: run the published app directly as a Windows Service (e.g. via NSSM, or
   by adding `Microsoft.Extensions.Hosting.WindowsServices` and calling `UseWindowsService()` in
   `Program.cs` — a code change, out of scope for this docs-only runbook, flagged above as not yet
   done) behind a separate reverse proxy such as IIS in pure-proxy mode or a Windows build of nginx.
   Bind Kestrel to loopback exactly as in the IIS case.
6. **Windows Firewall** — allow inbound 443 only on the public-facing NIC; the loopback-bound
   Kestrel port needs no explicit rule since it's not reachable off-host.

### PostgreSQL

1. **Install** via the official Windows installer (EDB) or `choco install postgresql`, matching the
   same current major version used elsewhere in this project.
2. **Bind and restrict access** the same way as Linux: `listen_addresses` limited to `localhost` (or
   the private network address of a separate database host), and `pg_hba.conf` entries scoped to
   the specific application server's address and role — never `0.0.0.0/0`.
3. **Service account** — the PostgreSQL Windows service runs under its own dedicated local service
   account (the installer creates one by default); do not run it under a domain administrator or
   the same account as the web application.
4. **Login role, database, tuning, backup, and maintenance** — identical guidance to the Linux
   PostgreSQL section above; the SQL and the operational practices don't differ by host OS, only the
   installer and default file locations do (`%PROGRAMFILES%\PostgreSQL\<version>\data` by default).
5. **Secrets on Windows** — store the connection string and any other credentials either as
   encrypted Application Pool environment variables (`appcmd` write access restricted to
   administrators) or through a secrets manager (Azure Key Vault, a self-hosted vault) fetched at
   service start. `dotnet user-secrets` is a development-only convenience and is **not** appropriate
   here. Never store credentials as plaintext in `web.config` or `appsettings.Production.json`
   checked into any repository.

## Cross-cutting items deferred to later hardening work

Per ADR 0014 and plan §9.1–§9.4, the following are explicitly out of scope for the initial
single-server release and are not addressed by this runbook: multi-node deployment, a managed
database service with automatic failover, distributed caching, and container orchestration. Revisit
only when a measured capacity or availability requirement justifies it.

### Per-instance state, and where it went

Four in-process stores once made a second web instance unsafe. **All four have been replaced**
(ADR 0066), so this section is now a description of the design rather than a warning. The
single-server topology in this runbook is unaffected either way — it simply no longer depends on
being single-server for correctness.

Note the split: **only two of the four moved into PostgreSQL.** The other two moved *outward*, to the
client, and one of them — ASP.NET Core session — was removed rather than relocated.

| Was | Now | Where it lives |
|---|---|---|
| Remembered per-page filter selections (`AddDistributedMemoryCache` + `AddSession`) | `src/JobTrack.Web/CookieFilterMemoryStore.cs` | **Client cookie**, protected and principal-bound, with key-count and payload bounds |
| Pending personal-access-token delivery (`PendingPatDeliveryStore`) | `src/JobTrack.Web/PendingPatDeliveryCookie.cs` | **Client cookie**, short-lived and actor-bound |
| Login attempt rate limiting | `PostgreSqlLoginAttemptRateLimiter` (`JobTrack.Identity`) | **PostgreSQL** bounded `rate_limit_window` |
| External API per-user rate limiting | `PostgreSqlApiRateLimitStore` (`JobTrack.Identity`) | **PostgreSQL** bounded `rate_limit_window` |
| Data-protection key ring (filesystem/GCS) | `PersistKeysToDbContext` | **PostgreSQL** `data_protection_key` |

**There is no server-side session store, in PostgreSQL or anywhere else.** `AddSession`,
`AddDistributedMemoryCache` and `UseSession` were deleted outright; nothing in `JobTrack.Web`
references `ISession`. Putting session into PostgreSQL was considered and rejected: the only consumer
was a small filter map, and a protected cookie carries it without adding a database round trip to
every request. Authentication itself was always a cookie, validated against the durable security
stamp — that has not changed.

The in-process implementations still exist (`LoginAttemptRateLimiter`, `InProcessApiRateLimitStore`)
and remain the default, selected by `RateLimiting:Store=InProcess`. They are correct for the
single-server topology this runbook describes. What changed is that choosing them under
`Deployment:Topology=MultiInstance` now **fails startup** instead of failing silently.

### Configuration matrix

Every setting below defaults to its single-instance value, so an existing deployment that sets none
of them keeps behaving exactly as this runbook describes.

| Setting | Single instance (this runbook, ADR 0014) | Multi-instance (ADR 0066) |
|---|---|---|
| `Deployment:Topology` | `SingleInstance` (or unset) | `MultiInstance` |
| `Database:Provider` | `PostgreSql` or `Sqlite` | `PostgreSql` **only** |
| `DataProtection:Store` | `FileSystem` (or unset) | `PostgreSql` |
| `DataProtection:KeyPath` | required outside Development | ignored; unnecessary |
| `RateLimiting:Store` | `InProcess` (or unset) | `PostgreSql` |
| `RateLimiting:MaxPartitionCount` | unused by the in-process store | 4096 by default, per purpose |
| Session affinity | irrelevant | must stay **off** |

Under `MultiInstance` the last three are not advisory: startup throws unless the provider and both
stores are PostgreSQL, so a half-configured revision never serves traffic. The reverse is not
enforced — PostgreSQL stores are perfectly valid on a single instance, and are the easier starting
point if a second instance is ever likely.

SQLite is rejected outright under `MultiInstance`; it remains fully supported single-instance.
