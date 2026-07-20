# Container image (local demo / smoke-testing)

A single-container image running `JobTrack.Web` against SQLite, intended for a quick throwaway
instance on a developer machine (this was built and verified under OrbStack on macOS/arm64). The
image is built from [`../../Dockerfile`](../../Dockerfile).

**This is not the project's deployment story.** [ADR 0014](../decisions/0014-single-server-deployment.md)
fixes a single bare-metal/VM server with a dedicated unprivileged service account behind a locally
managed reverse proxy, and explicitly defers containers and orchestration until a measured
requirement justifies them. [`production-deployment.md`](production-deployment.md) remains the real
runbook. Several choices below (a baked-in self-signed certificate, a placeholder trusted-proxy
address, an unencrypted data-protection key ring) are acceptable only because this image is a local
demo artifact — see "What makes this demo-only" at the end, which is the list to work through if
this ever needs to become a real deployment target. Most importantly, it ships a **known, published
demo credential** (`demo` / `demo1234`) baked in at build time, so it must never be exposed to a
network with a reachable demo account. The privileged `admin` account gets a **random** password
generated at build time (never a known default): unknown for a plain local `docker run` — where you
sign in as `demo` — and, on Cloud Run, an explicit random one the deploy script generates and prints
(see "Cloud Run smoke test"). Neither account forces a password change on first sign-in.

## Why SQLite

The two providers are mutually exclusive per deployment, not a failover pair, and SQLite is a fully
conforming backend rather than a reduced-feature fallback (see the README's "Dual-provider
persistence"). Choosing it here keeps the whole demo to one container with no separate database
server, no second image, and no orchestration — precisely the "embedded/single-node deployment
where running a separate PostgreSQL server isn't warranted" case SQLite exists for. Its operational
envelope is documented in [`sqlite-limitations-and-configuration.md`](sqlite-limitations-and-configuration.md).

## Build

**The build context is the monorepo root (`VSStuff/`), not `JobTrack/`.** `.editorconfig` lives one
level above `JobTrack/` (the git root is the parent directory), and the build runs with
`TreatWarningsAsErrors`. A context rooted at `JobTrack/` omits that file, so the analyzer
directory-walk misses its severity overrides and the build fails on rules the local build never
hits (`VSTHRD103`, downgraded to `none` there as a duplicate of `CA1849`). The Dockerfile copies
`.editorconfig` and the `JobTrack/` subtree separately to mirror that real layout inside the image.

Run from `JobTrack/`:

```bash
docker build -f Dockerfile -t jobtrack-web ..
```

`Dockerfile.dockerignore` (not `.dockerignore` — the name is context-relative and the context is the
parent) ignores everything by default and re-includes only `.editorconfig` and `JobTrack/`, so
sibling monorepo projects never enter the context.

The image is ~213MB, on a 131MB `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` base.

### Base image

Chiseled (distroless-style: no shell, no package manager, non-root by default). Two consequences
worth knowing before changing anything:

- **No shell.** `docker exec ... sh` and `ls` don't exist in the image; inspect a container with
  `docker cp` instead. A RID-targeted publish emits an apphost, so entrypoints invoke the binary
  directly rather than through the `dotnet` muxer, which the image also doesn't ship.
- **No ICU**, so the base defaults to `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`. This is safe
  here only because the codebase uses `CultureInfo.InvariantCulture` exclusively (there are no
  `CurrentCulture`/`CreateSpecificCulture` call sites) and Noda Time carries its own TZDB data in
  `NodaTime.TimeZones` rather than reading system ICU/tzdata. Code that starts depending on ICU must
  move to the `-chiseled-extra` tag; the failure mode would be a runtime culture exception, not a
  build error.

### Client-side assets

`wwwroot/lib/` (Bootstrap, Mulish) is gitignored and restored via LibMan rather than vendored, so
the build stage installs `Microsoft.Web.LibraryManager.Cli` and runs `libman restore` from
`src/JobTrack.Web` before publish. It is not copied from the build context, and the pinned versions
come from `libman.json` as usual — bump them there, never by hand.

## The three apphosts

The image bundles three framework-dependent, ReadyToRun apphosts under `WORKDIR /app`:

| Path | Purpose |
| --- | --- |
| `./web/JobTrack.Web` | the application (default `ENTRYPOINT`) |
| `./database/JobTrack.Database` | one-time schema deployment |
| `./admincli/JobTrack.AdminCli` | one-time administrator bootstrap, emergency password reset |

The two CLIs are reached with `--entrypoint`, which resolves relative to `WORKDIR /app` — hence the
`./database/...` and `./schema-versions` paths in the commands below.

### `ASPNETCORE_CONTENTROOT` is load-bearing

Because those apphosts sit in sibling subdirectories rather than at `/app` itself, the web app's
content root would otherwise default to the process working directory (`/app`) and `WebRootPath`
would resolve to the non-existent `/app/wwwroot`. **This fails silently:** `MapStaticAssets` still
matches its routes (the endpoint manifest is found next to the assembly, so requests return `200`
rather than `404`) but serves every asset as a zero-byte body — an unstyled site with no startup
error and nothing in the logs. `ENV ASPNETCORE_CONTENTROOT=/app/web` pins it; keep it in step with
the web apphost's directory if that ever moves. Confirm with the startup log line
`Content root path: /app/web`, and verify assets return real byte counts, not just `200`.

## HTTPS only

`Program.cs` sets the authentication cookie to `Secure`-only unconditionally — a deliberate
fail-closed default. Over plain HTTP the browser silently discards the cookie, so sign-in appears to
succeed server-side and then bounces straight back to the login page with no visible error (the same
trap documented in [`local-live-instance.md`](local-live-instance.md)). The image therefore listens
on HTTPS `:8443` only and clears the base image's default `ASPNETCORE_HTTP_PORTS=8080` rather than
leaving an unusable plaintext listener.

A self-signed certificate from `dotnet dev-certs` is generated at build time and baked in at
`/app/certs/devcert.pfx`. Browsers will warn; that is expected. The `--chown` on its `COPY` is
required, not cosmetic: `dev-certs` writes the file `0600` owned by root, which the non-root
`APP_UID` cannot read, and Kestrel then fails to bind at startup with
`UnauthorizedAccessException`.

## Run it

The image ships a pre-seeded demo database, so there is no setup:

```bash
docker run --rm -p 8443:8443 -v jobtrack-data:/app/data jobtrack-web
```

Open <https://localhost:8443> and sign in as the normal demo user:

| | |
| --- | --- |
| Username | `demo` |
| Password | `demo1234` |

The image bakes in **two** accounts (see "How the accounts and trees are seeded"): this `demo`
account — a normal, non-admin `JobManager`+`Worker` who owns the five sample job trees — and a
privileged `admin` account whose password is **random** (no known default; unknown for a local
`docker run` unless you passed `--build-arg ADMIN_PASSWORD`). For local use you want `demo`.

**Neither account forces a password change on first sign-in** — both are seeded with
`--no-force-password-change`, so `demo1234` works repeatedly rather than only once. That is a
deliberate demo affordance, not the ADR 0023 default, which every normally provisioned account still
gets.

`/app/data` holds both the SQLite database and the data-protection key ring. A *new* named volume is
empty, so Docker populates it from the image — which is how the seeded accounts and trees reach the
volume. An *existing* volume is left untouched, so any changes you make survive a
`docker rm` / `docker run` cycle.

## Persistence: what survives what

**Nothing is ever written back to the image.** It is immutable and acts purely as a seed. Everything
you create in the app — jobs, work sessions, employees, rates, audit rows, password changes — lands
in one SQLite file, `/app/data/jobtrack.db`, on the volume mounted there. The data-protection key
ring sits beside it in `/app/data/keys`.

| Action | Data |
| --- | --- |
| `docker stop` / `docker start` | kept |
| `docker rm` the container, `docker run` a new one on the same volume | **kept** — the container is disposable, the volume is not |
| `docker build` a new image (even with a different demo password) | kept, and the new seed is *ignored* — see below |
| `docker volume rm jobtrack-data` | **gone**, back to the pristine seed (both accounts + the five sample trees) |
| `docker run` with no `-v` | **gone on exit** — see the trap below |

Verified rather than assumed: a password changed through the browser survived
`docker rm -f jobtrack` followed by a fresh `docker run` on the same volume.

### Seeding only happens into an empty volume

A *new* named volume is empty, so Docker populates it from the image's `/app/data` — that is how the
seeded demo account gets there. An *existing* volume is never touched. The consequence catches
people out: **rebuilding the image does not update your database.** Change the demo credentials, add
a schema version, rebuild — an existing `jobtrack-data` keeps the old contents, and the app may then
run against a schema older than the binaries expect. `docker volume rm jobtrack-data` is what picks
up a new seed.

### The `-v` trap

`VOLUME /app/data` in the Dockerfile means Docker *always* gives that path a volume. Omit `-v` and
you get an **anonymous** one — so the app still works and still writes, but:

```bash
docker run --rm -p 8443:8443 jobtrack-web        # <-- every job you create is destroyed on exit
```

`--rm` removes the container *and its anonymous volumes*. Without `--rm` the data survives, but in a
volume with a random hex name that is hard to identify later. Always pass
`-v jobtrack-data:/app/data`.

### Backing up (and the WAL trap)

SQLite runs in WAL mode, so recent commits may live in `jobtrack.db-wal` and not yet be in the main
file. **Copying `jobtrack.db` alone gives a silently stale snapshot** — this is not theoretical; it
happened while writing this doc and showed a stale `requires_password_change` and
`access_failed_count`, which sent the diagnosis off in the wrong direction entirely. Take all three
files, or stop the container first so the WAL is checkpointed on close:

```bash
docker stop jobtrack
for f in jobtrack.db jobtrack.db-wal jobtrack.db-shm; do docker cp "jobtrack:/app/data/$f" .; done
docker start jobtrack
```

Back up `/app/data/keys` in step with the database. It is not optional convenience: restoring the
database without its key ring invalidates every existing session and antiforgery token
([`web-host-security.md`](web-host-security.md)).

For anything resembling real use, PostgreSQL and
[`postgresql-backup-restore.md`](postgresql-backup-restore.md) are the answer — a Docker volume is
not a backup strategy, and this image is not a deployment (see the top of this document).

### How the accounts and trees are seeded

Four steps run at build time (see the `/appdata` block in the Dockerfile), all through the shipped
`JobTrack.AdminCli`:

1. **Schema deploy** (`JobTrack.Database deploy`).
2. **Bootstrap the `admin`** (`AdminCli bootstrap`) — one atomic operation creating administrator
   id 1 and root job node id 1. Its password is random (a known default is never baked in): the
   `ADMIN_PASSWORD` build-arg if supplied, otherwise one generated from `/dev/urandom` in the build
   step and recorded nowhere. `--no-force-password-change` clears the ADR 0023 flag, since a forced
   change on a baked-in credential that reverts on every recycle is pointless friction.
3. **Create the `demo` user** (`AdminCli create-employee`) — a normal, non-admin
   `JobManager`+`Worker` employee (`demo` / `demo1234`), also `--no-force-password-change` so the
   published credential stays reusable. Employee id 2.
4. **Import the seven sample trees** (`AdminCli import-tree`, once per file in
   `samples/job-tree-imports/`) as `demo`, so the demo user — not the admin — owns them. Each lands a
   subtree under the root (`--parent-id` defaults to the root, id 1). The admin owns only the root
   node; `demo` owns everything below it.

Bootstrap takes its password non-interactively via `--password` (it still prompts for display
name / time zone / username, which the build pipes on stdin); `create-employee` and `import-tree`
are fully non-interactive. Passing a password in `argv` is visible in the process list and shell
history — an explicit trade-off accepted here for an automated demo-image build, exactly as
`BootstrapCommandOptions.Password` documents.

### Bootstrapping a different account by hand

Only needed against a database that has no administrator (the shipped one already has the `admin`
account). Interactive, so it needs `-it` and a real terminal — it cannot be scripted:

```bash
docker run --rm -it -v jobtrack-data:/app/data --entrypoint ./admincli/JobTrack.AdminCli \
  jobtrack-web bootstrap --provider sqlite \
  --connection-string "Data Source=/app/data/jobtrack.db"
```

Schema deployment is not idempotent against a database that already has JobTrack's tables, so this
pairs with a volume you have reset, not the seeded one. The deploy step itself, if you need it:

```bash
docker run --rm -v jobtrack-data:/app/data --entrypoint ./database/JobTrack.Database \
  jobtrack-web deploy --provider sqlite \
  --connection-string "Data Source=/app/data/jobtrack.db" --scripts-root ./schema-versions
```

## Configuration baked into the image

| Variable | Value | Why |
| --- | --- | --- |
| `ASPNETCORE_CONTENTROOT` | `/app/web` | see above — silent empty-asset bug without it |
| `Database__Provider` | `Sqlite` | one container, no separate server |
| `ConnectionStrings__JobTrackIdentity` | `Data Source=/app/data/jobtrack.db` | on the named volume |
| `DataProtection__KeyPath` | `/app/data/keys` | required outside Development; on the volume so a re-created container keeps the key ring |
| `ForwardedHeaders__KnownProxies__0` | `127.0.0.1` | required outside Development — a placeholder, see below |
| `Kestrel__Endpoints__Https__*` | `:8443`, baked cert | `Secure`-only cookie needs HTTPS |

`DataProtection:KeyPath` and the forwarded-headers settings are both fail-closed startup
requirements outside Development ([`web-host-security.md`](web-host-security.md)). Keeping the key
ring on the volume matters: lose it and every existing session and antiforgery token invalidates.

## Cloud Run smoke test (2026-07-17)

This image was smoke-tested on Google Cloud Run as a quick "does the container actually work
outside my machine" check, not as a deployment recommendation — see the top of this document and
[ADR 0014](../decisions/0014-single-server-deployment.md). The two things that made this safe to do
at all:

1. **The privileged `admin` account gets a random password** the script generates and prints, since
   Cloud Run is network-exposed and a known admin credential must never be reachable. The published
   `demo` / `demo1234` account is deliberately left as-is — it is a normal, non-admin user (owns the
   sample trees, no account-management rights), and its whole point is to be shareable. Any change a
   visitor makes to it is wiped back to the seed on the next recycle (see below).
2. **An HTTP endpoint added at deploy time, on top of the existing HTTPS one**, because Cloud Run's
   fully-managed product terminates TLS at Google's front end and always proxies to the container
   over plain HTTP on `$PORT` — it does not do TLS passthrough to a container-side certificate.
   `Program.cs` already supports this: `ForwardedHeaders` (`XForwardedFor`/`XForwardedProto`) runs
   before `UseHttpsRedirection()`, so a request forwarded with `X-Forwarded-Proto: https` is treated
   as already secure and `CookieSecurePolicy.Always` still works correctly. This only needed a
   deploy-time env var, not an image change:
   `Kestrel__Endpoints__Http__Url=http://+:8080` alongside the image's existing
   `Kestrel__Endpoints__Https__*` (which simply goes unused — nothing reaches the container on 8443
   through Cloud Run). The baked-in `ForwardedHeaders__KnownProxies__0=127.0.0.1` is likewise inert
   here; `ForwardedHeaders__KnownNetworks__0=0.0.0.0/0` is what actually does the job, and is
   reasonable specifically because Cloud Run does not allow direct public access to the container —
   only Google's own front end can ever be the thing setting those headers.

[`../../scripts/deploy-cloudrun.sh`](../../scripts/deploy-cloudrun.sh) does all of this —
build with a freshly generated `ADMIN_PASSWORD` (demo stays `demo1234`), push to Artifact Registry,
deploy — and prints **both** logins at the end, since nothing else records the random admin one:

```bash
./scripts/deploy-cloudrun.sh <gcp-project-id> [region]   # region defaults to europe-west1
```

`europe-west1` (Belgium) is a Tier 1 GCP pricing region, so the Always Free allowance and
per-unit cost both go further than in `europe-west2` (London), which is Tier 2 — pick that
default over a same-continent alternative that costs more for no functional benefit.

It assumes an existing Artifact Registry Docker repo named `cloud-run-source-deploy` in that
project/region (already present in this project from other services). `--platform linux/amd64` in
the script matters when building on Apple Silicon — Cloud Run's default runtime is `amd64`, and
OrbStack's local Docker defaults to the host's `arm64`.

**The admin password is different on every run.** The script always generates a fresh one and passes
it via `--build-arg ADMIN_PASSWORD`; nothing pins it between deploys, and it is printed once at the
end because it is recorded nowhere else. The demo password stays the published `demo1234`.

**This deployment has no persistent volume.** Cloud Run containers are stateless and ephemeral —
`/app/data` is just the image's writable layer, so every cold start (scale-to-zero is the default,
hence `--min-instances=0`) goes back to the baked-in seed with the random password above, and a
second concurrent instance (never happens here — `--max-instances=1` — but would on a higher limit)
would not share state with the first. That is a materially different persistence model from the
named-volume setup described everywhere else in this document, and is fine only because this was a
short-lived reachability check, not a running demo meant to hold state.

**Nothing you do in the running app is durable — every recycle wipes the database back to the baked
seed.** This is the crucial difference from the named-volume setup, and it is easy to get wrong. Any
change made through the browser — the demo password, new accounts, jobs, work sessions, anything —
is written only to the container's throwaway filesystem and is destroyed, not preserved, the moment
Cloud Run replaces the instance. Recycles are routine and not on a fixed schedule: a scale-to-zero
followed by the next visit's cold start, a redeploy, or a maintenance/load recycle Cloud Run decides
on its own. After any of them the filesystem is restored from the image and the database is back to
exactly its initial seed state.

Two consequences that specifically catch people out:

- **Changing a password to something memorable does not stick.** Sign in as `demo`, set the password
  to `helloworld`, and it lasts only until the next recycle — then it reverts to the baked `demo1234`.
  The password is not "preserved"; it is wiped like everything else and *restored to* the baked seed
  value, which for demo happens to be the same `demo1234` as before (and for admin, the same random
  build-time value the deploy script printed). To bake in a different demo password you must rebuild
  with `--build-arg DEMO_PASSWORD=...` and redeploy.
- **Passwords only "rotate" on a rebuild, not on a recycle.** A plain scale-to-zero cold start
  restores whatever was baked into the current image (demo → `demo1234`, admin → its build-time
  random). A fresh `deploy-cloudrun.sh` run bakes a *new* random admin password — which is why a
  previously distributed admin password stops working after you redeploy; the demo one does not
  change.

If you actually need live changes to persist, this ephemeral-filesystem path is the wrong tool: use
a real backing store (PostgreSQL / Cloud SQL, or a mounted volume), which this demo deliberately does
not wire up.

**Tear down when done** — this is a real, billed, publicly reachable Cloud Run service for as long
as it exists:

```bash
gcloud run services delete jobtrack-web --project=<project-id> --region=<region used to deploy> --quiet
```

## What makes this demo-only

Each of these is fine for a throwaway local instance and unacceptable for a real deployment. They
are the gap list, not a backlog anyone has committed to:

- **It ships a known, published credential** (`demo` / `demo1234`), seeded at build time and
  published in this document. It is only a normal, non-admin user, but it is still a reachable
  sign-in — the strongest reason the image must never be network-exposed with that account live. (The
  privileged `admin` account is safer by construction: its password is random, never a known default,
  and on Cloud Run the deploy script generates and prints a fresh one per deploy.) A real deployment
  provisions its administrator interactively, once, on the target host, and seeds no published
  credential at all.
- **The certificate is self-signed and its password is in the image**, visible in `docker history`.
  BuildKit flags this (`SecretsUsedInArgOrEnv`) and the warning is correct; it is tolerated only
  because the cert protects nothing. A real deployment terminates TLS at the reverse proxy with a
  real certificate and does not carry one in the image at all.
- **`ForwardedHeaders__KnownProxies__0=127.0.0.1` names no real proxy.** Kestrel here is reached
  directly, so nothing ever arrives with forwarded headers to trust; the value exists purely to
  satisfy the fail-closed check. Behind an actual proxy this must name that proxy's own address —
  a wrong value here is a spoofable client address/scheme, which is exactly what the check exists to
  prevent.
- **The data-protection key ring is unencrypted at rest** (`No XML encryptor configured` at
  startup, expected here), and lives in a Docker volume rather than a directory restricted to a
  dedicated service account.
- **No reverse proxy, no HSTS in front, no backup of the volume**, and SQLite's single-node
  envelope applies.
- The container runs as the image's non-root `APP_UID`, which is the one hardening property that
  does carry over.
