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
| `provision` | `aspnet:10.0-noble` + `postgresql-client` | `JobTrack.Database`, `JobTrack.AdminCli`, and `provision.sh`. Executed as a transient Cloud Run job, then deleted. |

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

The deploy script deletes the job immediately after a successful execution and also from its EXIT
trap on failure. It simultaneously revokes the provisioning identity's Cloud SQL and Secret Manager
access. Those grants also carry a 30-minute `request.time` IAM condition, so they become ineffective
without cleanup if the deploy workstation is killed. A later deploy recreates both pieces only
immediately around schema execution, so there is no dormant privileged job to execute between
deployments. Emergency recovery uses the same pattern with a 15-minute expiry around its five-minute
job deadline.

The expiry bounds the *privilege*; it does not remove the *entry*. Each run titles its condition
`jobtrack-provision-<nonce>`, and revoking a conditional binding requires matching that condition
exactly — so a run killed before its EXIT trap runs leaves behind a binding no later run can name,
because every run builds a different title. Left alone those accumulate as dead policy entries. Each
deploy therefore starts by reading the project and secret IAM policies back and removing every
`jobtrack-provision-*` condition held by the provisioning identity, before granting its own. The
sweep is silent when there is nothing to reconcile and tolerates secrets that do not exist yet.

## What this deployment contains

**No example job nodes.** No sample trees, no requester scenario, no `JobTrack.UatSeed`. The database
after provisioning holds the schema, the roles, and three accounts. The only job node is the
permanent root that `bootstrap` creates (id 1, owned by the administrator) — the tree starts empty
beneath it.

**Three accounts, all with randomly generated passwords.** Nothing is a known default, and nothing is
baked into the image:

| Account | Roles | Standing rota | Purpose |
| --- | --- | --- | --- |
| `adminpg` | `Administrator` | 08:00–20:00, Mon–Sun | Account and role management; owns the root node. Created by `AdminCli bootstrap`. |
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
secret mount. The deploy script prints their Secret Manager names, not their values; retrieve a value
only when handing that one-time credential to its intended user.

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

A fifth login role exists alongside these but backs no connection string — no running service ever
holds it:

| Login role | Group role | Used by |
| --- | --- | --- |
| `jobtrack_emergency_reset_login` | `jobtrack_emergency_reset` | `docker/emergency-reset.sh`, invoked ad hoc (see below) |

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
argument or in `pg_stat_activity`'s query text either. The host-side `gcloud sql` password operations
use a mode-0600 temporary flags file, so the Cloud SQL administrator password does not enter the
deploying workstation's process arguments either.

### Identity and least privilege

Neither workload runs as the **default compute service account**, which is created holding the
project `Editor` role in most projects — running a publicly reachable web app as it would mean an
application compromise carried write access to every resource in the project. Three purpose-made
service accounts are created instead, starting with no roles at all:

| Service account | Runs | Can read | Can write |
| --- | --- | --- | --- |
| `jobtrack-run` | the Cloud Run service | four application connection strings and the data-protection certificate/password | key-ring bucket objects only |
| `jobtrack-provision-sa` | the transient provisioning job | database admin password, five role passwords, three account passwords | nothing outside the database |
| `jobtrack-emergency-reset` | the transient recovery job | emergency-reset role password only | nothing outside the database |

The runtime identity holds `roles/cloudsql.client`; the other two receive it only around their job
execution and lose it in the cleanup trap. The role grants *connect and authenticate* only — not a
database privilege. What each can actually do inside the database is still decided by the PostgreSQL
role its connection string authenticates as.

The split is the point: the running application cannot read the Cloud SQL admin password, so
compromising it does not escalate to the PostgreSQL superuser, and it cannot read the three account
passwords either. The provisioning job holds the mirror image and no connection-string secret. Every
grant is per-resource, not project-wide.

**Co-tenancy rule.** No other workload in this project may run as an identity holding any role on
the key-ring bucket, the four connection-string secrets, or the Cloud SQL instance. The project's
demo services (`jobtrack-web`, the SQLite smoke test) were relocated to a dedicated
`jobtrack-demo-projects` project rather than trusted to hold no privileges here indefinitely — see
[`../plans/2026-08-06-cloudrun-persistent-isolation-plan.md`](../plans/2026-08-06-cloudrun-persistent-isolation-plan.md).
That plan also found and removed a standing project-wide `roles/cloudbuild.builds.builder` grant on
the **default compute service account**, which included `storage.objects.*` on every bucket in the
project (including the key-ring bucket) and was never load-bearing for any workload here.

### Network exposure of the database

The Cloud SQL instance has **no authorized networks** and connector enforcement is `REQUIRED`, so its
public IP accepts no direct client: every connection arrives through the Cloud SQL connector,
authenticated with IAM. `--ssl-mode=ENCRYPTED_ONLY` sits behind that as defence in depth. Every deploy
reconciles these settings, including on an existing instance, so console drift is removed.

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
`jobtrack-run` is granted `roles/storage.objectUser`; legacy `objectAdmin` access is removed.

The XML key payloads are encrypted with an RSA certificate loaded from two Secret Manager file
mounts: a PKCS#12 archive and a separate password secret. They mount into **separate directories**
(`.../certificate/` and `.../certificate-password/`), not two files side by side: Cloud Run backs
each secret file mount with its own directory volume and rejects a second, different secret mounted
into a directory already in use. The paths are baked into the image as
`DataProtection__CertificatePath`/`DataProtection__CertificatePasswordPath` and mirrored by the
deploy script's `certificate_mount_path`/`certificate_password_mount_path`; an architecture test
asserts the two agree and stay in distinct directories. Production startup fails closed if either
path is absent. Existing material is validated on each deployment; if only half the pair exists or
the password cannot open the archive, deployment stops rather than generating a certificate that
could make the persisted key ring unreadable.

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
- **A buildx builder on the `docker-container` driver.** The default `docker` driver cannot produce
  the SBOM and `--provenance=mode=max` attestations the deploy script requires, and fails with
  `Attestation is not supported for the docker driver`. Create one once per workstation:

  ```bash
  docker buildx create --name jobtrack-builder --driver docker-container --use
  ```

  The script deliberately does not do this itself: buildx builders are host-level configuration with
  their own cache lifecycle, not a per-deployment resource.
- An Artifact Registry Docker repository named `cloud-run-source-deploy` in the target region. The
  script creates it if absent.
- APIs: Cloud Run, Cloud SQL Admin, Secret Manager, Artifact Registry, Container Scanning,
  On-Demand Scanning, Binary Authorization, Artifact Analysis, and Cloud KMS (the scripts enable
  them).
- A one-time, project-wide Binary Authorization setup. Run this deliberately before the first
  deployment; it creates the KMS signing key and attestor and changes the default policy to require
  a JobTrack release attestation. It refuses to replace an existing customized admission policy:

  ```bash
  ./scripts/configure-cloudrun-binary-authorization.sh <gcp-project-id> [region]
  ```

  Its last step applies the `run.allowedBinaryAuthorizationPolicies` organization-policy constraint.
  That needs `roles/orgpolicy.policyAdmin`, which project ownership alone does not grant, so the step
  is **non-fatal**: without the role the script warns, prints the command for an organization policy
  administrator to run, and still exits 0. JobTrack's own releases are gated either way — every
  deployment passes `--binary-authorization=default`, so the fail-closed policy imported above is
  evaluated against each image.

  **This constraint does not make Binary Authorization mandatory project-wide** — a live test
  confirmed a deploy that simply omits `--binary-authorization` is not evaluated against the
  attestation policy, constraint applied or not (Cloud Run has no org-policy control that forces
  every deploy through Binary Authorization; the constraint only restricts which *value* the flag
  may be set to, and `"default"` is the only legal value regardless). See
  [`../plans/2026-08-06-cloudrun-persistent-isolation-plan.md`](../plans/2026-08-06-cloudrun-persistent-isolation-plan.md)
  §2.2/§4 for the test. What actually keeps `jobtrack-web-pg` attested-only is that this deploy
  script always passes the flag — script discipline, not policy enforcement. Apply the constraint
  anyway (it is still the documented intent and narrows what an explicit override could request),
  but do not rely on it as the control.

## Deploy

```bash
./scripts/deploy-cloudrun-postgresql.sh <gcp-project-id> [region]   # region defaults to europe-west1
```

`europe-west1` (Belgium) is Tier 1 GCP pricing; `europe-west2` (London) is Tier 2 and costs more for
no functional benefit.

**The working tree must be clean**, or the script refuses to start: image tags embed the commit hash,
so a dirty tree would publish an immutable tag naming a commit that does not describe what is inside
the image. Commit (or stash) before deploying — including doc-only edits.

The script is **idempotent and re-runnable**. Every secret keeps its existing value rather than being
regenerated, so a second run redeploys the current image without locking you out of an existing
database. Mutable resource controls are reconciled on every run rather than only at creation.

What it does, in order:

1. Enables the required APIs and ensures the Artifact Registry repository exists with immutable tags
   and vulnerability scanning enabled.
2. Creates the Cloud SQL instance (`db-f1-micro`, PostgreSQL 18, seven retained backups, 30-day PITR,
   final backup on deletion, deletion protection, `ssl-mode=ENCRYPTED_ONLY`, connector enforcement
   required, no authorized networks) and the `jobtrack` database, if absent. Those controls are
   patched onto an existing instance too. The
   major version deliberately tracks the local development instance (`postgresql@18`) rather than
   trailing it.
3. Generates and stores in Secret Manager, if absent: the Cloud SQL admin password, five login-role
   passwords, three account passwords, and the data-protection certificate/password pair.
4. Builds and pushes both images (`--target provision` and the default `serve`) under unique immutable
   tags, including SBOM and maximum-mode provenance attestations, then resolves them to digests.
5. Scans both digests and fails closed on any HIGH or CRITICAL vulnerability, then creates a
   KMS-signed release attestation for each digest. Cloud Run service and job deployments opt into the
   fail-closed Binary Authorization policy.
6. Creates three dedicated service accounts and grants each only what it needs (see "Identity and
   least privilege" below).
7. Stages the new service digest as an unaddressable, no-traffic candidate. On an upgrade, the old
   revision continues to serve while Cloud Run proves the candidate can start.
8. Grants 30-minute provisioning access immediately before deploying and executing the
   **provisioning job** by digest — schema deploy, roles, grants, functions, five login roles, then
   the three accounts — and deletes the job and revokes its access when it finishes.
9. Stages a fresh no-traffic revision against the upgraded schema, requests its login page through
   the candidate-only URL, and promotes that exact tag to 100% only after the smoke test succeeds.
10. Prints the service URL, usernames, roles, and secret names; credential values stay out of deploy
   logs.

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

**Backups.** Cloud SQL automated backups plus point-in-time recovery are enabled. The script fixes
seven retained automated backups, seven days of transaction logs, a 30-day final-backup retention
period, and retained backups on deletion. An owner must still define and test the environment's
RPO/RTO.
[`postgresql-backup-restore.md`](postgresql-backup-restore.md) remains the schema-level
restore-verification procedure.

**Rotating a role password.** Change the Secret Manager version and re-run the script: role passwords
are reapplied by step 2 of provisioning. *Account* passwords are not — `bootstrap` and
`create-employee` are skipped once the accounts exist. For those, use the account's own
change-password page, or the emergency path below when it can't sign in to reach that page.

**Recovering a locked or inaccessible account.** `docker/emergency-reset.sh` runs `AdminCli
reset-password`/`reset-2fa` as `jobtrack_emergency_reset_login` — a login role provisioning creates
every run (idempotent, like the other four) but that backs no application connection string, so no
running service ever holds it. Invoke the dedicated helper, which creates a transient job under the
emergency identity and revokes its narrowly scoped access in an EXIT trap:

```bash
./scripts/emergency-reset-cloudrun-postgresql.sh <project> password <username> [region]
```

(`two-factor` instead of `password` for a lost authenticator device.) `reset-password` prints a one-time
temporary password to the job's logs (`gcloud run jobs executions logs <execution-id>` if `--wait`'s
own output is missed) and forces a change at next sign-in; both commands clear any existing lockout
and revoke every live personal access token for that account (ADR 0029) — the whole point of an
emergency reset is recovering an account the normal flow can't reach, so it must not hand back a
credential that still can't sign in.

**Schema upgrades.** Re-running the script rebuilds the images and re-executes the provisioning job,
which applies any new schema-version scripts before the new service revision receives traffic. The
script stages the digest first, then applies the schema, stages and smoke-tests a fresh candidate,
and finally promotes that exact revision. Failed candidates remain at zero traffic.

This is an expand/contract deployment contract, because a database transaction and Cloud Run's
traffic control cannot be atomic. Every schema version shipped with an application release must
remain compatible with the revision already serving traffic. A destructive or contracting change
belongs in a later release, after the old revision has been retired; combining it with the first
code rollout would reintroduce the old-revision/new-schema failure window.

**Teardown.** The service, the Cloud SQL instance, the bucket, and the secrets are all
billed for as long as they exist, and the Cloud SQL instance is the expensive one (a `db-f1-micro`
runs continuously; it does not scale to zero with the service):

```bash
gcloud run services delete jobtrack-web-pg --project=<project> --region=<region> --quiet
gcloud sql instances patch jobtrack-pg --project=<project> --no-deletion-protection --quiet
gcloud sql instances delete jobtrack-pg --project=<project> --quiet
gcloud storage rm -r gs://<project>-jobtrack-dpkeys
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

- **`ForwardedHeaders__KnownNetworks__0=0.0.0.0/0`.** Correct for Cloud Run specifically, because the
  container is not directly reachable and only Google's front end can set those headers — but it is a
  blanket trust that would be a spoofable client address and scheme anywhere else.
- **The Cloud SQL admin password exists in Secret Manager.** A stricter setup would use IAM database
  authentication and hold no password at all. It is unreadable by the service, and the provisioning
  identity can access it only during a deploy.
- **One instance, by necessity** — see above. No horizontal scaling until the four in-process stores
  are replaced.
- **No OpenTelemetry, no alerting, no log-based monitoring** beyond what Cloud Run gives for free
  (plan §9.2 work, not done anywhere yet).
- **Binary Authorization attestation is enforced by this deploy script's own flag, not by any GCP
  policy control.** No Cloud Run org policy can make attestation mandatory for every deploy in the
  project (confirmed by live negative test,
  [`../plans/2026-08-06-cloudrun-persistent-isolation-plan.md`](../plans/2026-08-06-cloudrun-persistent-isolation-plan.md)
  §2.2/§4) — a future deploy path that omits `--binary-authorization=default` would bypass
  attestation silently. Accepted residual, not something the `run.allowedBinaryAuthorizationPolicies`
  constraint closes despite its name.
- **ADR 0014 still says single-server, no containers.** Nothing here amends it. If this path becomes
  the intended deployment for real, that is an ADR to write, not a script to run.
