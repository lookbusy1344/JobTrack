# Persistent PostgreSQL deployment on Google Cloud Run

**The production deployment topology**
([ADR 0062](../decisions/0062-cloud-run-cloud-sql-production-topology.md)), and its operational
runbook. A **second** container configuration, alongside — not replacing — the SQLite demo image
documented in [`docker-image.md`](docker-image.md). That image bakes a seeded SQLite database into its own
filesystem, so every Cloud Run recycle throws away whatever the app wrote. This one keeps no state in
the container at all: it runs against a managed PostgreSQL instance (Cloud SQL) that outlives every
revision, cold start, and redeploy.

Built from [`../../Dockerfile.postgresql`](../../Dockerfile.postgresql); deployed by
[`../../scripts/deploy-cloudrun-postgresql.sh`](../../scripts/deploy-cloudrun-postgresql.sh). The
original `Dockerfile` and `scripts/deploy-cloudrun.sh` are untouched and remain the demo path.
PostgreSQL-only host settings, including mandatory Secure antiforgery and TempData cookies, are set
by the PostgreSQL deploy script and do not change the SQLite demo's same-as-request cookie policy.

**This is a supported production topology.** ADR 0062 made Cloud Run + Cloud SQL, deployed only by
the script above, a production choice alongside ADR 0014's single-server topology. ADR 0066 then
added the two-instance variant without retiring either earlier choice. The remaining accepted
residuals are listed at the end; stale descriptions of this path as a demo or pilot are defects.

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
                    │   state, service max=2,      │        └────────────────────┘
                    │   no session affinity)       │
                    └───┬──────────────────────┬───┘
                        │                      │
              ┌─────────▼────────┐   ┌─────────▼────────┐
              │ container        │   │ container        │   ... up to service max
              │ instance A       │   │ instance B       │
              └─────────┬────────┘   └─────────┬────────┘
                        └──────────┬───────────┘
                       Unix socket │ /cloudsql/<instance>
                          ┌────────▼─────────────────────┐
                          │ Regional Cloud SQL           │
                          │ PostgreSQL (HA standby)      │
                          │  domain + identity +         │
                          │  data_protection_key +       │
                          │  rate_limit_window +         │
                          │  rate_limit_capacity_lock    │
                          └──────────────────────────────┘

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

After all three users have completed that change, retire the now-obsolete Secret Manager versions:

```bash
./scripts/retire-cloudrun-enrolment-secrets.sh <project> --confirm-passwords-changed
```

The command disables rather than destroys the versions, providing a recoverable retention window.
Later deployments omit retired credentials and do not recreate them. Provisioning queries the
database first and fails closed if an account is unexpectedly missing without an enrolment secret.

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

Set `JOBTRACK_ROTATE_DATABASE_CREDENTIALS=true` for an explicit credential-rotation deployment. It
adds new versions for the administrator and five login roles, rebuilds the four derived connection
strings, applies the role passwords in the provisioning job, and promotes the matching revision.
Because PostgreSQL password roles cannot accept old and new passwords concurrently, schedule this as
a maintenance change: the old serving revision loses database access between the role update and
candidate promotion. Do not leave the variable enabled for routine deployments.

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
service accounts are created instead, starting with no roles at all; the unused default compute
identity is disabled and user-managed key creation/upload is blocked by organization policy:

| Service account | Runs | Can read | Can write |
| --- | --- | --- | --- |
| `jobtrack-run` | the Cloud Run service | four application connection strings and the data-protection certificate/password | nothing outside the database |
| `jobtrack-provision-sa` | the transient provisioning job | database admin password, five role passwords, and any still-enabled enrolment passwords | nothing outside the database |
| `jobtrack-emergency-reset` | the transient recovery job | emergency-reset role password only | nothing outside the database |

The runtime identity holds `roles/cloudsql.client`; the other two receive it only around their job
execution and lose it in the cleanup trap. The role grants *connect and authenticate* only — not a
database privilege. What each can actually do inside the database is still decided by the PostgreSQL
role its connection string authenticates as.

The split is the point: the running application cannot read the Cloud SQL admin password, so
compromising it does not escalate to the PostgreSQL superuser, and it cannot read the three account
passwords either. The provisioning job holds the mirror image and no connection-string secret. Every
grant is per-resource, not project-wide.

Human control-plane access follows a different rule: use a managed organization group with enforced
phishing-resistant MFA and reviewed membership, plus a separately controlled break-glass path. Do
not leave a personal consumer account as the sole project Owner. Routine deployers should receive
the narrow roles needed by the deployment workflow rather than Owner; release-signing permission on
`jobtrack-release/image-attestation` should be held separately where staffing permits. Review group,
deployer, signer and break-glass membership quarterly and after every personnel change.

**Co-tenancy rule.** No other workload in this project may run as an identity holding any role on
the four connection-string secrets, the data-protection certificate, or the Cloud SQL instance —
which since the key ring moved into `data_protection_key` now also guards the key ring itself. The
project's
demo services (`jobtrack-web`, the SQLite smoke test) were relocated to a dedicated
`jobtrack-demo-projects` project rather than trusted to hold no privileges here indefinitely — see
[`../plans/2026-08-06-cloudrun-persistent-isolation-plan.md`](../plans/2026-08-06-cloudrun-persistent-isolation-plan.md).
That plan also found and removed a standing project-wide `roles/cloudbuild.builds.builder` grant on
the **default compute service account**, which included `storage.objects.*` on every bucket in the
project (including the key-ring bucket that existed at the time) and was never load-bearing for any
workload here.

`scripts/harden-cloudrun-postgresql-project.sh`, invoked by every deploy, also removes the unused
default VPC and its internet-wide SSH/RDP/ICMP rules, disables the default compute identity, removes
the legacy Cloud Build identity's builder role, and deletes only known-empty obsolete source buckets.
Every destructive target is exact; a Compute workload, forwarding rule, peering, or non-empty bucket
fails the cleanup rather than widening its scope.

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

The key ring must outlive any single container, and under multiple instances every instance must read
the *same* one: lose it and every session cookie and antiforgery token is invalidated, and — the part
that bites hardest — every enrolled TOTP secret becomes undecryptable.

It lives in the **`data_protection_key` table in PostgreSQL** (`DataProtection:Store=PostgreSql`,
`PersistKeysToDbContext`), the same database the application already depends on. Only the
`jobtrack_identity` role is granted access to that table; the domain role has none, and both grants
are pinned by tests. `MultiInstance` topology refuses to start on any other store.

An earlier revision mounted a Cloud Storage bucket at `/var/lib/jobtrack/keys` over GCS FUSE. That is
retired: it left correctness resting on FUSE filesystem semantics and required a second shared
service that the local multi-instance topology would also have had to reproduce. When removing it,
note that `gcloud run deploy` merges — the volume **and** its mount must both be removed explicitly,
and the bucket must not be deleted until no live or rollback-target revision still mounts it.

> **A key ring is credential data, not a cache.** ADR 0066 decision #6: any deployment holding an
> existing ring migrates it, preserving every key. This deployment took a recorded one-off exception —
> its ring was discarded with operator consent — and the consequence is documented under
> "Recovering a locked or inaccessible account": with 2FA enabled on every account, *all* users were
> locked out of password sign-in, not merely prompted to re-enrol.

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

## Multiple instances, and what makes that safe

This service runs with the service-level cap `--max=2`, explicit `--concurrency=80`, and **no session
affinity** (ADR 0066). It previously ran with revision-level `--max-instances=1` as a correctness
constraint, because four in-process stores failed *silently*
under a second instance. All four now have shared or stateless equivalents:

| Was in-process | Now |
| --- | --- |
| remembered page filters | protected, principal-bound cookie |
| pending PAT delivery | short-lived protected cookie |
| login rate limiter | bounded `rate_limit_window` in PostgreSQL, atomic consume |
| external API rate limiter | the same shared primitive |
| data-protection key ring | `data_protection_key` in PostgreSQL, certificate-encrypted |

None of this is left to convention. `Deployment:Topology=MultiInstance` **fails startup** unless
`Database:Provider`, `DataProtection:Store` and `RateLimiting:Store` are all PostgreSQL, so a
misconfigured revision never serves. The deploy script also asserts session affinity is off, and the
provisioning job refuses to proceed if the database reports fewer connections than the pool budget
assumes.

The limiter admits at most `RateLimiting:MaxPartitionCount` live partitions per purpose (4096 by
default). `rate_limit_capacity_lock` serializes only new-partition admission; callers consume
existing partitions concurrently. Neither limiter table is granted directly to an application role:
the EF-mapped SECURITY DEFINER function is the sole write boundary.

Service-level `--min=0` remains the default: scale to zero costs nothing and a cold start loses only a
few seconds.

**Verified in production (2026-08-09).** Two instances served simultaneously — overlapping windows on
distinct instance ids in the request log — across 10,000 requests with zero failures. One scenario
resisted production measurement: a *forced* cross-host round trip (page rendered on one instance,
form posted to another). Cloud Run exposes no per-instance addressing, so correlated probe pairs all
landed on a single instance. That case is covered deterministically by the two-host integration
fixture and the OrbStack compose topology (`scripts/multi-instance-test.sh`).

### Checking how many instances are actually serving

Cloud Run identifies an instance by an opaque **instance id**, surfaced on every request log entry as
`labels.instanceId`. Counting distinct ids is the direct evidence; the autoscaler's configuration is
not, because the service-level maximum is a ceiling and says nothing about how many ran.

Two instances only serve concurrently when demand needs them. Container concurrency is 80, so a load
of 40 parallel requests is absorbed by one instance and proves nothing — drive more than 80 in
flight. A fixed floor makes the test deterministic:

```bash
project=<gcp-project-id>; region=europe-west1
url="$(gcloud run services describe jobtrack-web-pg --project="$project" --region="$region" \
       --format='value(status.url)')"

# Hold a floor of two so the test does not depend on autoscaler timing, then exceed concurrency.
gcloud run services update jobtrack-web-pg --project="$project" --region="$region" --min=2
ab -n 3000 -c 160 -q "$url/Account/Login"

# Distinct instances, each with the window it served in.
gcloud logging read 'resource.type="cloud_run_revision"
    AND resource.labels.service_name="jobtrack-web-pg"
    AND httpRequest.requestUrl:"/Account/Login"' \
  --project="$project" --freshness=3m --limit=3000 \
  --format='csv[no-heading](labels.instanceId,timestamp)' \
| awk -F, '{id=substr($1,1,18); t=$2
            if(!(id in mn)||t<mn[id])mn[id]=t; if(!(id in mx)||t>mx[id])mx[id]=t; c[id]++}
       END {for(i in c) printf "%s... %6d reqs  %s -> %s\n", i, c[i], substr(mn[i],12,12), substr(mx[i],12,12)}'

gcloud run services update jobtrack-web-pg --project="$project" --region="$region" --min=0
```

**Read the windows, not just the count.** Distinct ids alone prove only that several instances existed
at some point during the window — an instance replaced mid-test produces a second id without two ever
running together. Concurrency is proven when the windows *overlap*:

```
001548f729b139637f...   2858 reqs  18:10:13.018 -> 18:10:22.256
001548f729a6234732...    142 reqs  18:10:13.282 -> 18:10:21.753   <- contained in the above
```

The traffic split is normally lopsided, as here. Cloud Run fills one instance toward its concurrency
target before spilling over; an even split is not expected and its absence is not a fault.

`run.googleapis.com/container/instance_count` in Cloud Monitoring shows the same thing as a time
series, and is the better tool for watching ordinary production rather than a deliberate test.

### Instances have no addressable IP

There is no per-instance address, and no `gcloud run instances` command. This is not an oversight to
work around — it is why a *forced* cross-host test is impossible from outside, and why the evidence
above is id-based.

`httpRequest.serverIp` in the logs is tempting and misleading: it is the Google front end, identical
for every instance.

```
001548f729a6234732 -> serverIp 2600:1900:4242:200::
001548f729b139637f -> serverIp 2600:1900:4242:200::   <- same address, different instances
```

`httpRequest.remoteIp` is the *client*. To exercise deliberate host-to-host behaviour, use the
OrbStack topology (`scripts/multi-instance-test.sh`), which publishes `web-a` and `web-b` on separate
ports precisely so a test can choose which host receives each request.

### No liveness probe, deliberately

Only a TCP **startup** probe is configured. Cloud Run addresses probes to the container with a Host
header that is not this deployment's public name, and `AllowedHosts` answers 400 before routing sees
the path — so an `httpGet` probe fails every check and the revision never becomes ready. `gcloud`
offers no way to set a probe Host header, and `--liveness-probe` accepts only `httpGet`/`grpc`, so a
liveness probe has no satisfiable form here.

The consequence is worth knowing: startup is proven only as far as "the listener accepts
connections", and **nothing restarts a process that wedges while still holding its socket**.
`/health/live` and `/health/ready` remain the honest signals for any checker able to send a real Host
header. Closing this means giving host filtering a way to admit the probe — an application change.

> Both probe and volume settings must be passed *explicitly*, including empty values to remove them.
> `gcloud run deploy` **merges** into the existing revision, so simply dropping a flag leaves whatever
> an earlier revision configured. A stale `httpGet` liveness probe survived that way once and had
> Cloud Run shutting an instance down roughly every 90 seconds.

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

  The deploy script's preflight creates this builder if it is missing, and refuses to continue if a
  builder of that name exists on the wrong driver. It also passes `--builder` explicitly on every
  build rather than relying on which builder happens to be selected — a workstation whose active
  builder had drifted to the default `docker` driver otherwise failed mid-deployment, after Cloud SQL
  had already been patched.
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
export JOBTRACK_MONITORING_NOTIFICATION_CHANNEL=projects/<project>/notificationChannels/<id>
./scripts/deploy-cloudrun-postgresql.sh <gcp-project-id> [region]   # region defaults to europe-west1
```

The notification channel must already be enabled and must not be explicitly unverified. See
[`monitoring-and-alerts.md`](monitoring-and-alerts.md) for alert ownership, thresholds and response.

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
2. Creates the regional-HA Cloud SQL instance (`db-custom-1-3840`, PostgreSQL 18, seven retained backups, seven-day PITR,
   final backup on deletion, deletion protection, `ssl-mode=ENCRYPTED_ONLY`, connector enforcement
   required, no authorized networks) and the `jobtrack` database, if absent. Those controls are
   patched onto an existing instance too. The
   major version deliberately tracks the local development instance (`postgresql@18`) rather than
   trailing it.
3. Generates and stores in Secret Manager, if absent: the Cloud SQL admin password, five login-role
   passwords, initial account enrolment passwords, and the data-protection certificate/password pair.
   Retired enrolment secrets are not recreated.
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
10. Reconciles targeted Data Access logging, six alert policies, and their verified notification
    channel; removes unused default-project residue; then prints the service URL and any still-enabled
    enrolment-secret names. Credential values stay out of deploy logs.

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

**An unreadable key ring locks out password sign-in, not just 2FA.** Observed 2026-08-09. If the
data-protection key that encrypted an account's TOTP secret is gone, `GetAuthenticatorKeyAsync`
throws a `CryptographicException` from inside `PasswordSignInAsync` — Identity probes the available
two-factor providers *before* reporting on the password — so the request 500s and the user sees the
generic error page. The symptom looks nothing like a 2FA problem, and no account with 2FA enabled can
sign in by any route. Diagnose it from the exception, which names the missing key id:

```bash
gcloud logging read 'resource.type="cloud_run_revision"
    AND resource.labels.service_name="jobtrack-web-pg"
    AND textPayload:"CryptographicException"' --project=<project> --freshness=30m --limit=1
```

The fix is `two-factor` reset for **every** affected account; they then sign in with the password
alone and re-enrol. Two practical notes from doing it under pressure:

- The reset runs one Cloud Run job under a fixed name, so accounts must be reset **sequentially**.
- It selects the newest tagged provisioning image. Running it *while a deployment is in flight* can
  select an image that has been pushed but not yet attested, which Binary Authorization denies. Wait
  for the deploy to finish, or re-run afterwards.

**Schema upgrades.** Re-running the script rebuilds the images and re-executes the provisioning job,
which applies any new schema-version scripts before the new service revision receives traffic. The
script stages the digest first, then applies the schema, stages and smoke-tests a fresh candidate,
and finally promotes that exact revision. Failed candidates remain at zero traffic.

This is an expand/contract deployment contract, because a database transaction and Cloud Run's
traffic control cannot be atomic. Every schema version shipped with an application release must
remain compatible with the revision already serving traffic. A destructive or contracting change
belongs in a later release, after the old revision has been retired; combining it with the first
code rollout would reintroduce the old-revision/new-schema failure window.

**Teardown.** The service, the Cloud SQL instance, and the secrets are all billed for as long as they
exist, and the Cloud SQL instance is the expensive one (`db-custom-1-3840` is a dedicated-core tier
running continuously; it does not scale to zero with the service, and it is not free-tier eligible —
the tier is set by the connection budget, not by cost):

```bash
gcloud run services delete jobtrack-web-pg --project=<project> --region=<region> --quiet
gcloud sql instances patch jobtrack-pg --project=<project> --no-deletion-protection --quiet
gcloud sql instances delete jobtrack-pg --project=<project> --quiet
```

There is no key-ring bucket to remove: the data-protection key ring lives in the `data_protection_key`
table and is deleted with the instance.

Secrets are cheap but hold live credentials — delete them too if the environment is gone for good.

## Testing this locally instead

There is no local Docker Compose counterpart, deliberately. `Program.cs` marks the authentication
cookie `Secure` unconditionally, so any local container path needs TLS termination in front of it,
and the developer guide already covers running `JobTrack.Web` against a local PostgreSQL instance
directly (`scripts/run-web.sh`, and "Running on a development server → PostgreSQL"). That is a
shorter loop than a compose stack for the same coverage.

## Accepted residuals and next hardening boundary

Shorter than the demo image's list — the published credentials, the baked-in certificate, and the
ephemeral database are all gone — but not empty:

- **`ForwardedHeaders__KnownNetworks__0=0.0.0.0/0`.** Correct for Cloud Run specifically, because the
  container is not directly reachable and only Google's front end can set those headers — but it is a
  blanket trust that would be a spoofable client address and scheme anywhere else.
- **The Cloud SQL admin password exists in Secret Manager.** A stricter setup would use IAM database
  authentication and hold no password at all. It is unreadable by the service, and the provisioning
  identity can access it only during a deploy.
- **Release approval is not independent while one operator can administer the project, sign with the
  KMS key and deploy.** Managed group ownership and a separate signer narrow this; small-team
  deployments that deliberately combine the duties must treat the owner credential as the highest
  value secret and require phishing-resistant MFA.
- **Minimal observability, not full telemetry.** Built-in metrics, targeted Data Access logs and the
  mandatory alert baseline are deployed; full OpenTelemetry tracing remains trigger-based.
- **Binary Authorization attestation is enforced by this deploy script's own flag, not by any GCP
  policy control.** No Cloud Run org policy can make attestation mandatory for every deploy in the
  project (confirmed by live negative test,
  [`../plans/2026-08-06-cloudrun-persistent-isolation-plan.md`](../plans/2026-08-06-cloudrun-persistent-isolation-plan.md)
  §2.2/§4) — a future deploy path that omits `--binary-authorization=default` would bypass
  attestation silently. Accepted residual, not something the `run.allowedBinaryAuthorizationPolicies`
  constraint closes despite its name.
- ~~**ADR 0014 still says single-server, no containers.**~~ Resolved:
  [ADR 0062](../decisions/0062-cloud-run-cloud-sql-production-topology.md) makes this path a
  supported production topology, with this script as its only sanctioned deploy path.
