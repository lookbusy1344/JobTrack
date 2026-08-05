# Persistent PostgreSQL deployment on Google Cloud Run

A **second** container configuration, alongside — not replacing — the SQLite demo image documented in
[`docker-image.md`](docker-image.md). That image bakes a seeded SQLite database into its own
filesystem, so every Cloud Run recycle throws away whatever the app wrote. This one keeps no state in
the container at all: it runs against a managed PostgreSQL instance (Cloud SQL) that outlives every
revision, cold start, and redeploy.

Built from [`../../Dockerfile.postgresql`](../../Dockerfile.postgresql); deployed by
[`../../scripts/deploy-cloudrun-postgresql.sh`](../../scripts/deploy-cloudrun-postgresql.sh). The
original `Dockerfile` and `scripts/deploy-cloudrun.sh` are untouched and remain the demo path.

**Still not the project's blessed deployment topology.**
[ADR 0014](../decisions/0014-single-server-deployment.md) fixes a single bare-metal/VM server behind
a locally managed reverse proxy and defers containers and managed database services explicitly;
[`production-deployment.md`](production-deployment.md) is the runbook that decision produces. What
this document adds is a *persistent*, credential-hygienic container path for the cases the demo image
cannot serve — a shared evaluation instance, a UAT environment, a pilot — without pretending to be
the production story. The gap list is at the end.

## Why Cloud SQL rather than PostgreSQL inside the image

"A second image with a persistent PostgreSQL instance" has two possible shapes, and only one of them
actually persists on Cloud Run:

| Shape | Verdict |
| --- | --- |
| PostgreSQL in the same container, data on the container filesystem | **Not persistent.** Cloud Run's container filesystem is an in-memory overlay destroyed on every recycle — exactly the failure mode this exists to fix. It also counts against the instance memory limit. |
| PostgreSQL in the same container, data on a mounted Cloud Run volume | Cloud Storage FUSE is not a POSIX filesystem (no `fsync` durability guarantees, no file locking as PostgreSQL needs it) and PostgreSQL on it is unsupported and corrupts. NFS/Filestore works technically, but its smallest instance costs an order of magnitude more per month than the whole rest of this deployment. |
| **PostgreSQL as a managed instance (Cloud SQL), app in the container** | **What this does.** Durable storage, automated backups and point-in-time recovery, no database process in the request-serving container, and the same `Database:Provider=PostgreSql` code path the real single-server topology uses. |

The container therefore stays stateless, which is what Cloud Run is actually good at, and the
database is a separate managed resource with its own lifecycle.

## What is deployed

```
                    ┌──────────────────────────────┐
   browser ──HTTPS──▶ Cloud Run front end (TLS)    │
                    └──────────────┬───────────────┘
                       plain HTTP, X-Forwarded-Proto: https
                    ┌──────────────▼───────────────┐        ┌────────────────────┐
                    │ Cloud Run service            │        │ Secret Manager     │
                    │  jobtrack-web-pg             │◀───────│ 4 connection       │
                    │  (chiseled, non-root, no     │        │ strings + secrets  │
                    │   state, max-instances=1)    │        └────────────────────┘
                    └───┬──────────────────────┬───┘
       Unix socket      │                      │  GCS FUSE volume
   /cloudsql/<instance> │                      │  /var/lib/jobtrack/keys
                    ┌───▼──────────────┐   ┌───▼──────────────────┐
                    │ Cloud SQL        │   │ GCS bucket           │
                    │ PostgreSQL       │   │ data-protection keys │
                    └──────────────────┘   └──────────────────────┘

   one-time: Cloud Run **job** jobtrack-provision (same build, provisioning image)
             → schema deploy, login roles, three accounts
```

Two images come out of one `Dockerfile.postgresql`, sharing a single build stage:

| Target | Base | Runs |
| --- | --- | --- |
| `serve` (default) | `aspnet:10.0-noble-chiseled` | `JobTrack.Web` only. No shell, no package manager, non-root, nothing else in the image. |
| `provision` | `aspnet:10.0-noble` + `postgresql-client` | `JobTrack.Database`, `JobTrack.AdminCli`, and `provision.sh`. Executed **once**, as a Cloud Run job, then idle. |

Splitting them keeps the internet-facing service on the hardened base while the provisioning path
gets the shell, `psql`, and stdin plumbing it needs — the request-serving image ships neither the
admin CLI nor a shell to run it with.

### Why provisioning is a separate job here, and isn't in the SQLite demo

The two configurations provision at opposite ends of the lifecycle, and everything else follows from
that:

| | SQLite demo (`Dockerfile`) | Persistent (`Dockerfile.postgresql`) |
| --- | --- | --- |
| Provisioning runs | at `docker build`, in a `RUN` step | at deploy, as a Cloud Run job |
| Where the seeded database lives | inside the image, at `/app/data` | in Cloud SQL, outside every container |
| Serving image carries the admin CLI | yes — it is how the image self-seeds | **no** — only the job image has it |
| Survives a recycle | no; the image re-inflates its baked seed | yes |

The demo can bake its database into the image because SQLite *is* a file, so build-time seeding and
runtime state are the same thing — which is also exactly why nothing it does persists. A managed
PostgreSQL server outlives every container and cannot be baked into one, and baking credentials into
an image that talks to a live database would be the wrong shape regardless. So provisioning has to
happen once, against the real instance, after it exists.

That has a security dividend worth stating plainly: the demo image ships `JobTrack.AdminCli` and
`JobTrack.Database` in the same image that serves public traffic, because it has to. Here, the
tooling that can create administrators lives only in the job image, which never handles a request.

The job is free to keep — Cloud Run jobs bill only while executing — and deleting it is equally safe,
since the deploy script recreates it with `--execute-now` on every run. Keeping it is what lets a
later redeploy apply new schema versions before the new revision serves.

## What this deployment contains

**No example job nodes.** No sample trees, no requester scenario, no `JobTrack.UatSeed`. The database
after provisioning holds the schema, the roles, and three accounts. The only job node is the
permanent root that `bootstrap` creates (id 1, owned by the administrator) — the tree starts empty
beneath it.

**Three accounts, all with randomly generated passwords.** Nothing is a known default, and nothing is
baked into the image:

| Account | Roles | Standing rota | Purpose |
| --- | --- | --- | --- |
| `admin` | `Administrator` | 08:00–20:00, Mon–Sun | Account and role management; owns the root node. Created by `AdminCli bootstrap`. |
| `manager` | `JobManager`, `Worker` | 09:00–17:00, Mon–Fri | Builds and runs the job tree. |
| `worker` | `Worker` | 09:00–17:00, Mon–Fri | Picks up and works nodes. |

All three carry a **£20/hour** default rate and a rota effective from 2020-01-01, so backdated work
still falls inside covered working time and cost reporting resolves from the first session. Rotas are
set by `AdminCli set-schedule` during provisioning (step 5 below) and edited afterwards in the Rota
pages; rates are edited in **Admin → Manage employee account**.

Usernames, display names, roles, rotas, the hourly rate, and the time zone are all variables at the
top of the deploy script.
All three passwords are generated by the deploy script (24 characters from `openssl rand`, well over
`PasswordPolicy.MinimumLength`'s 15), stored in Secret Manager, handed to the provisioning job over a
secret mount, and **printed once** at the end of the deploy run.

**All three force a password change on first sign-in** — the ADR 0023 default. This is the deliberate
difference from the demo image, which passes `--no-force-password-change` because its baked-in
credentials revert on every recycle. Here the change sticks, so the generated password is a one-time
enrolment credential rather than the account's standing password.

## Credential separation

The four connection strings `Program.cs` requires under PostgreSQL (security review remediation §2.6)
get four *separate* login roles, each a member of exactly one group role from
`database/postgresql/roles/jobtrack-roles-and-grants.sql`:

| Connection string | Login role | Group role |
| --- | --- | --- |
| `ConnectionStrings:JobTrackDomain` | `jobtrack_domain_login` | `jobtrack_domain` |
| `ConnectionStrings:JobTrackIdentity` | `jobtrack_identity_login` | `jobtrack_identity` |
| `ConnectionStrings:JobTrackPatManagement` | `jobtrack_pat_management_login` | `jobtrack_pat_management` |
| `ConnectionStrings:JobTrackPatAuthentication` | `jobtrack_pat_authentication_login` | `jobtrack_pat_authentication` |

Each gets its own random password. The service never holds the Cloud SQL admin (`postgres`)
credential at all — only the provisioning job does, and only for the length of one execution.

`JobTrack.AdminCli` runs as `jobtrack_domain_login` during provisioning, per
[`production-deployment.md`](production-deployment.md): `jobtrack_domain` is a strict superset of what
`bootstrap` and `create-employee` touch.

**Secrets never reach `argv`.** `JobTrack.Database` and `JobTrack.AdminCli` reject a
`--connection-string` containing a password, and `bootstrap`/`create-employee` reject a plaintext
`--password`. `provision.sh` writes each connection string to a `umask 077` file under `/tmp` and
passes `--connection-string-file`, and pipes each account password to `--password-stdin`. The login
roles are created through `psql`'s `\getenv`, so the role passwords never appear in a `psql -c`
argument or in `pg_stat_activity`'s query text either.

### Identity and least privilege

Neither workload runs as the **default compute service account**, which is created holding the
project `Editor` role in most projects — running a publicly reachable web app as it would mean an
application compromise carried write access to every resource in the project. Two purpose-made
service accounts are created instead, starting with no roles at all:

| Service account | Runs | Can read | Can write |
| --- | --- | --- | --- |
| `jobtrack-run` | the Cloud Run service | the four application connection-string secrets | the key-ring bucket |
| `jobtrack-provision-sa` | the provisioning job | the database admin password, four role passwords, three account passwords | nothing outside the database |

Both hold `roles/cloudsql.client`, which grants *connect and authenticate* only — not a database
privilege. What each can actually do inside the database is still decided by the PostgreSQL role its
connection string authenticates as.

The split is the point: the running application cannot read the Cloud SQL admin password, so
compromising it does not escalate to the PostgreSQL superuser, and it cannot read the three account
passwords either. The provisioning job holds the mirror image and no connection-string secret. Every
grant is per-resource, not project-wide.

### Network exposure of the database

The Cloud SQL instance has **no authorized networks**, so its public IP accepts no direct client:
every connection arrives through the Cloud SQL connector, authenticated with IAM. `--ssl-mode=
ENCRYPTED_ONLY` sits behind that as defence in depth — if an authorized network were ever added by
hand, an unencrypted connection still would not be accepted.

A private-IP-only instance would be stricter still, but needs a VPC with private services access and
Direct VPC egress on the Cloud Run service. That is a materially larger moving-parts budget for a
gap the two controls above already close for anything short of a Google-internal attacker.

### Transport security

The container reaches Cloud SQL over the Unix domain socket the Cloud SQL connector mounts at
`/cloudsql/<project>:<region>:<instance>`, so every connection string uses `Host=/cloudsql/...`.
`PostgreSqlTransportSecurity.Validate` exempts Unix-socket and loopback connections from the
`SSL Mode=VerifyFull` + `Root Certificate` requirement, and that exemption is honest here: the traffic
never leaves the instance's network namespace, and the connector authenticates the instance itself.

A public-IP TCP connection would *not* satisfy the validator in practice — Cloud SQL server
certificates carry the instance connection name, not the IP address, so `VerifyFull` against an IP
literal fails hostname verification. The socket is the right answer, not a shortcut around the check.

## Data-protection key ring

`DataProtection:KeyPath` is a fail-closed startup requirement outside Development
([`web-host-security.md`](web-host-security.md)), and the key ring must outlive the container: lose
it and every session cookie and antiforgery token is invalidated — every signed-in user is silently
logged out on the next recycle.

The deploy script creates a Cloud Storage bucket and mounts it at `/var/lib/jobtrack/keys` as a
Cloud Run GCS FUSE volume. The key ring is a handful of XML files written rarely and read at startup,
which is squarely inside what GCS FUSE handles well (unlike a database's write pattern — see the
table at the top).

The bucket is created with **public access prevention** (so it cannot be made world-readable, even
by mistake), **uniform bucket-level access** (removing per-object ACLs as a second, easily-missed way
to grant it), and **object versioning** — the last being recovery rather than security: an
overwritten or truncated key ring signs every user out, and a previous version restores it. Only
`jobtrack-run` is granted object access.

**The key ring is unencrypted at rest at the application level** (`No XML encryptor configured` at
startup). It is encrypted by Google-managed keys at the bucket level, and the bucket is private, but
this is not the same thing as an XML encryptor and is on the gap list below.

## `--max-instances=1` is load-bearing, not a cost setting

`production-deployment.md` §"In-process state that breaks under a second web instance" lists four
in-process stores — remembered page filters, the login rate limiter, pending PAT delivery, and the
external API rate limiter. Under two instances they fail *silently*: stale filters, rate limits that
effectively multiply by instance count, an occasional PAT that never displays. Nothing refuses to
start.

Persisting the database does not fix any of that; it is a separate piece of work (a shared cache or
database-backed stores). Until then this service is pinned to one instance, and raising the limit is
a code change first.

`--min-instances=0` is fine and is the default here: scale to zero costs nothing and a cold start now
loses nothing but a few seconds.

## Prerequisites

- `gcloud` authenticated (`gcloud auth login`) against a project with billing enabled.
- A local Docker daemon (OrbStack on this machine). The build passes `--platform linux/amd64` —
  Cloud Run runs amd64 and Apple Silicon defaults to arm64.
- An Artifact Registry Docker repository named `cloud-run-source-deploy` in the target region. The
  script creates it if absent.
- APIs: Cloud Run, Cloud SQL Admin, Secret Manager, Artifact Registry, Cloud Build (script enables
  them).

## Deploy

```bash
./scripts/deploy-cloudrun-postgresql.sh <gcp-project-id> [region]   # region defaults to europe-west1
```

`europe-west1` (Belgium) is Tier 1 GCP pricing; `europe-west2` (London) is Tier 2 and costs more for
no functional benefit.

The script is **idempotent and re-runnable**. Every resource is created only if absent, and every
secret keeps its existing value rather than being regenerated — so a second run prints the same three
passwords, redeploys the current image, and does not lock you out of an existing database.

What it does, in order:

1. Enables the required APIs and ensures the Artifact Registry repository exists.
2. Creates the Cloud SQL instance (`db-f1-micro`, PostgreSQL 18, automated backups and PITR on,
   `ssl-mode=ENCRYPTED_ONLY`, no authorized networks) and the `jobtrack` database, if absent. The
   major version deliberately tracks the local development instance (`postgresql@18`) rather than
   trailing it.
3. Generates and stores in Secret Manager, if absent: the Cloud SQL admin password, four login-role
   passwords, and three account passwords.
4. Builds and pushes both images (`--target provision` and the default `serve`).
5. Creates two dedicated service accounts and grants each only what it needs (see "Identity and
   least privilege" below).
6. Deploys and executes the **provisioning job** — schema deploy, roles, grants, functions, four
   login roles, then the three accounts. Waits for it and fails the whole run if it fails.
7. Deploys the **service** with the four connection strings mounted from Secret Manager, the GCS
   volume mounted at the key path, and `--max-instances=1`.
8. Prints the service URL and the three credentials.

### What provisioning actually runs

`provision.sh` in the provisioning image, in this order, each step skipped if already done:

1. `JobTrack.Database deploy --provider postgresql` as the Cloud SQL admin user. This applies every
   unapplied `schema-versions/NNNN_*.sql` script (it is idempotent — already-applied versions are
   skipped under the deployment advisory lock), then re-applies the roles-and-grants script and the
   stored functions. It creates the eight `NOLOGIN` group roles.
2. `psql` creates the four `LOGIN` roles, sets their passwords, and grants each its group role.
   Re-running resets the passwords to the current secret values, which is how a credential rotation
   is applied.
3. `AdminCli bootstrap` creates the administrator and the permanent root node — skipped if
   `initialised_marker` already has its row.
4. `AdminCli create-employee` twice, as the administrator — each skipped if that username already
   exists in `identity_user`. The hourly rate is passed explicitly even though
   `EmployeeProvisioningDefaults` already applies the same £20: what a deployment's accounts are
   worth per hour should be visible in the deployment, not inherited silently from a library constant
   that could later change.
5. `AdminCli set-schedule` three times, giving each account its standing rota — the administrator
   08:00–20:00 across all seven days, the other two 09:00–17:00 Monday to Friday. Every account is
   *already* created with `EmployeeProvisioningDefaults`' Mon–Fri 09:00–17:00, so this replaces that
   placeholder rather than adding beside it (a plain add collides on `schedule-version-overlap`).
   Re-running is idempotent in effect, and the command refuses outright once an account has more than
   one version or any exception — so a rota edited through the Rota pages survives a redeploy.

Objects are owned by the Cloud SQL admin user rather than `jobtrack_owner` (Cloud SQL grants no true
superuser, and the group role is `NOLOGIN` by design). The application-facing privilege separation is
unaffected: every grant in the roles script is explicit and per-table, and no application role holds
DDL rights.

## Operating it

**Backups.** Cloud SQL automated backups plus point-in-time recovery are enabled at instance
creation. That is the mechanism; the RPO/RTO and retention policy are a decision for whoever owns the
environment, not something this script fixes.
[`postgresql-backup-restore.md`](postgresql-backup-restore.md) remains the schema-level
restore-verification procedure.

**Rotating a password.** Change the Secret Manager version and re-run the script: role passwords are
reapplied by step 2 of provisioning. *Account* passwords are not — `bootstrap` and `create-employee`
are skipped once the accounts exist. Use `AdminCli reset-password` (through the provisioning job with
an overridden command) or the account's own change-password page.

**Schema upgrades.** Re-running the script rebuilds the images and re-executes the provisioning job,
which applies any new schema-version scripts before the new service revision goes live. Note the
ordering risk that implies: the old revision briefly serves against the new schema. For anything
where that matters, execute the job and deploy the service as two deliberate steps rather than one
script run.

**Teardown.** The service, the job, the Cloud SQL instance, the bucket, and the secrets are all
billed for as long as they exist, and the Cloud SQL instance is the expensive one (a `db-f1-micro`
runs continuously; it does not scale to zero with the service):

```bash
gcloud run services delete jobtrack-web-pg --project=<project> --region=<region> --quiet
gcloud run jobs delete jobtrack-provision --project=<project> --region=<region> --quiet
gcloud sql instances delete jobtrack-pg --project=<project> --quiet
gsutil rm -r gs://<project>-jobtrack-dpkeys
```

Secrets are cheap but hold live credentials — delete them too if the environment is gone for good.

## Testing this locally instead

There is no local Docker Compose counterpart, deliberately. `Program.cs` marks the authentication
cookie `Secure` unconditionally, so any local container path needs TLS termination in front of it,
and the developer guide already covers running `JobTrack.Web` against a local PostgreSQL instance
directly (`scripts/run-web.sh`, and "Running on a development server → PostgreSQL"). That is a
shorter loop than a compose stack for the same coverage.

## What still separates this from a production deployment

Shorter than the demo image's list — the published credentials, the baked-in certificate, and the
ephemeral database are all gone — but not empty:

- **The data-protection key ring has no XML encryptor.** Encrypted at rest by GCS, not by the
  application, and readable by anything with object access to the bucket.
- **`ForwardedHeaders__KnownNetworks__0=0.0.0.0/0`.** Correct for Cloud Run specifically, because the
  container is not directly reachable and only Google's front end can set those headers — but it is a
  blanket trust that would be a spoofable client address and scheme anywhere else.
- **The Cloud SQL admin password exists in Secret Manager.** A stricter setup would use IAM database
  authentication and hold no password at all. It is at least unreadable by the service — only the
  provisioning job's identity can access it.
- **One instance, by necessity** — see above. No horizontal scaling until the four in-process stores
  are replaced.
- **No OpenTelemetry, no alerting, no log-based monitoring** beyond what Cloud Run gives for free
  (plan §9.2 work, not done anywhere yet).
- **ADR 0014 still says single-server, no containers.** Nothing here amends it. If this path becomes
  the intended deployment for real, that is an ADR to write, not a script to run.
