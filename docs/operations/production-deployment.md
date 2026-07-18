# Production deployment (Linux and Windows Server)

**Closes (in part):** Implementation plan §9.1's "provisioning" and general hosting portion of the
operations runbooks. This document is the host-level runbook: OS service setup, reverse proxy
placement, and PostgreSQL provisioning for the single-server topology fixed by
[ADR 0014](../decisions/0014-single-server-deployment.md). It complements, and does not repeat:

- [`web-host-security.md`](web-host-security.md) — the application's own fail-closed configuration
  (`ForwardedHeaders:*`, `DataProtection:KeyPath`) and the Kestrel-level request limits it enforces.
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
- **`DataProtection:KeyPath`** must be an absolute path outside the deployment directory, created
  ahead of time, writable only by the service account (see `web-host-security.md`).
- **`ForwardedHeaders:KnownProxies` / `KnownNetworks`** must list the reverse proxy's own address —
  the loopback address it connects from, not a public range.
- **PostgreSQL login role.** The repository ships group roles only
  (`database/postgresql/roles/jobtrack-roles-and-grants.sql`: `jobtrack_owner`,
  `jobtrack_schema_deployer`, `jobtrack_application`, `jobtrack_readonly`,
  `jobtrack_emergency_reset`) and holds no environment credentials. Create an actual `LOGIN` role
  per environment and grant it membership in the appropriate group role — `jobtrack_application`
  for the running app's connection string, `jobtrack_schema_deployer` only for the deploy step, and
  `jobtrack_readonly` for reporting/auditor access. Never run the application itself as
  `jobtrack_owner` or a superuser.

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
   Environment=ForwardedHeaders__KnownProxies__0=127.0.0.1
   EnvironmentFile=/etc/jobtrack/secrets.env
   NoNewPrivileges=true
   ProtectSystem=strict
   ReadWritePaths=/var/lib/jobtrack

   [Install]
   WantedBy=multi-user.target
   ```

   `/etc/jobtrack/secrets.env` (holding `ConnectionStrings__JobTrackIdentity=...`) should be
   `chmod 600`, owned by `jobtrack:jobtrack`, and excluded from any config management repo that
   isn't itself a secret store. Enable and start with
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
3. **Create the login role and database**, then deploy schema and roles as described in the
   README's "Running on a development server → PostgreSQL" section, against this server instead of
   a local one:

   ```sql
   CREATE ROLE jobtrack_app_login LOGIN PASSWORD '...' IN ROLE jobtrack_application;
   CREATE DATABASE jobtrack
       OWNER jobtrack_app_login
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
   disabling or hand-tuning autovacuum preemptively.

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
   - Set `DataProtection:KeyPath` to the restricted directory from step 3, and
     `ConnectionStrings:JobTrackIdentity` via a protected mechanism (see "secrets" below) rather than
     a plaintext `appsettings.Production.json` committed anywhere.
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
