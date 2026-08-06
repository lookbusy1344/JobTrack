# Cloud Run persistent-deployment isolation plan

**Date:** 2026-08-06
**Status:** Implemented (2026-08-06), with §2.2 downgraded from "closed" to "narrowed" after a live
negative test disproved the original assumption about the org-policy constraint — see §2.2/§3.3/§4.
**Scope:** Findings from the 2026-08-06 live audit of the Google Cloud project hosting
`jobtrack-web-pg` (the persistent PostgreSQL deployment,
[`../operations/postgresql-cloud-run-deployment.md`](../operations/postgresql-cloud-run-deployment.md)).
The audit verified the persistent deployment's own controls are in place as designed — Cloud SQL
connector enforcement, per-secret IAM, key-ring bucket protection, Binary Authorization by
attestation, no leaked provisioning grants. What it found instead is that the two *demo* services
sharing the project undermine that design from outside it. This plan closes those paths.

**Decision context.** The demo services (`jobtrack-web`, the SQLite demo image, and `enrolment-web`
from the sibling EnrolmentRules project) are disposable and low-risk *by policy* — they may share a
project and a blast radius with each other. The persistent deployment may not share theirs: it holds
real state (Cloud SQL, the data-protection key ring, live credentials in Secret Manager), so nothing
a demo compromise yields may reach any resource the persistent deployment depends on.

## 1. Assessment

Two substantial findings, one structural observation. The common shape: the persistent deployment's
identity separation is careful, but IAM is project-scoped, and two co-tenant services hold (via the
default compute service account) project-wide permissions that cross into the persistent
deployment's resources.

Severity order is **High** > **Medium** > **Low**.

## 2. Findings

### 2.1 Demo services run as the default compute service account, which can read and overwrite the persistent deployment's data-protection key ring

| | |
|---|---|
| **Severity** | **High** |
| **Evidence (live, 2026-08-06)** | `gcloud run services describe jobtrack-web` and `enrolment-web` both report `serviceAccountName: 716005672573-compute@developer.gserviceaccount.com`; the project IAM policy grants that account `roles/cloudbuild.builds.builder` project-wide |

`roles/cloudbuild.builds.builder` includes `storage.objects.get`, `storage.objects.create`,
`storage.objects.delete`, and `storage.objects.list` — granted at project level, so it applies to
**every bucket in the project**, including `<project>-jobtrack-dpkeys`, the persistent deployment's
data-protection key ring. It also includes Artifact Registry upload.

The escalation chain: both demo services are publicly reachable; `jobtrack-web` deliberately ships
`JobTrack.AdminCli` and a shell in its serving image with published credentials (that is its
documented demo design). A remote-code-execution compromise of either demo yields the default
compute SA's token via the metadata server, which can then:

- **read** `jobtrack-web-pg`'s data-protection keys — enough to decrypt/forge authentication
  cookies and antiforgery tokens for the *persistent* instance and impersonate any signed-in user;
- **overwrite or delete** the key ring — signing every persistent-instance user out (versioning
  mitigates recovery, not the attack);
- **upload images** to the shared Artifact Registry repository (Binary Authorization stops them
  *running* as `jobtrack-web-pg`, but see §2.2).

The bucket's own protections (public access prevention, uniform bucket-level access, `objectUser`
for `jobtrack-run` only) are all present and all irrelevant here: the access arrives through a
*project-level* IAM grant, which uniform bucket-level access honours.

Neither demo needs this role. Both deploy scripts build locally with Docker buildx and push
directly; nothing in either demo's serving path uses Cloud Build, Cloud Storage, or Artifact
Registry at runtime. The grant is simply the project-creation default that
`postgresql-cloud-run-deployment.md` §"Identity and least privilege" already refuses for the
persistent workloads — the demos just never got the same treatment.

### 2.2 The Binary Authorization opt-out remains open — and, contrary to the deploy script's own comment, the organization-policy constraint cannot close it

| | |
|---|---|
| **Severity** | **Medium** |
| **Evidence (live, 2026-08-06)** | `gcloud resource-manager org-policies list --project=<project>` returns no policies; `jobtrack-web` is deployed with no `run.googleapis.com/binary-authorization` annotation and a mutable-style `:latest` image reference. **After applying the constraint** (§3.3 below), a live negative test — `gcloud run deploy` with an unattested public image (`us-docker.pkg.dev/cloudrun/container/hello`) and no `--binary-authorization` flag — still succeeded, with an empty `binary-authorization` annotation on the resulting revision |

`configure-cloudrun-binary-authorization.sh` treats its last step —
`run.allowedBinaryAuthorizationPolicies` — as non-fatal because `roles/orgpolicy.policyAdmin` is not
implied by project ownership, and its warning path evidently triggered on the original run. Its
own comment describes the constraint as stopping "a Cloud Run deployer opting out of Binary
Authorization altogether." **That description does not match GCP's actual behaviour, confirmed by
live test after applying the constraint (§3.3):** `run.allowedBinaryAuthorizationPolicies` is a
*list policy* restricting which value `--binary-authorization` may be **set to** (only `"default"`
is permitted) — not a control that makes attestation mandatory. Cloud Run only evaluates Binary
Authorization for a revision that explicitly opts in with `--binary-authorization=default`;
omitting the flag (the default, and what `deploy-cloudrun.sh` and `enrolment-web`'s deploy script
both do) bypasses the check entirely, constraint or no constraint. There is no Cloud Run org-policy
control that forces every deploy in a project through Binary Authorization — the fail-closed
protection on `jobtrack-web-pg` exists solely because its own deploy script always passes
`--binary-authorization=default`, i.e. by script discipline, not by policy.

The org-policy blocker described in the original script comment has been resolved anyway (the
project **is** parented by an organization, `<ORG_NAME>`/`453382479060`, and the deploying
account can self-grant `roles/orgpolicy.policyAdmin` there), and the constraint has been applied
per-project (§3.3) since it does still narrow what value the flag can take and documents intent —
but it closes none of the risk this finding describes. §2.1's Artifact Registry write permission
combined with this gap means a compromise that also obtained Cloud Run deploy rights in the project
could still push and run an arbitrary unattested image; nothing found today grants those deploy
rights to a demo identity, so this remains one missing layer rather than a demonstrated open chain.

### 2.3 Structural: project-scoped IAM means demo drift can silently re-open §2.1

| | |
|---|---|
| **Severity** | **Low** (with §2.1 and §2.2 fixed) |
| **Evidence** | The three services, one Artifact Registry repository, one Secret Manager namespace, and one Binary Authorization policy all share `project-e2ce9938-0f7b-48a8-b0d` |

Fixing §2.1 removes today's cross-tenant permissions, but nothing *prevents* tomorrow's: any future
project-level grant to a demo identity (a console experiment, another default) lands in the same
IAM policy the persistent deployment lives under. Full isolation means separate projects. Moving
the **demos** out is cheap — they are stateless and redeploy from their scripts in minutes; moving
the **persistent** deployment would mean migrating Cloud SQL data, secrets, the key ring, KMS, and
the attestor. If separation is done, the demos move, not the database.

This stage is deliberately severable: §3.1–§3.3 close every concrete finding on their own, and the
accepted policy is that the demos *may* share the persistent project's blast radius provided they
hold no privileges inside it. §3.4 exists for defence against future IAM drift, not against any
path that remains open after §3.1–§3.3.

## 3. Remediation stages

Stages 3.1–3.3 are live `gcloud` operations plus script/doc reconciliation, in dependency order.
Each is idempotent and independently verifiable. Stage 3.4 is optional and decided separately.

### 3.1 Move both demo services onto a dedicated no-role service account — closes §2.1's active path

1. Create one shared demo identity in the project, granted **nothing**:

   ```bash
   gcloud iam service-accounts create demo-run \
     --project=project-e2ce9938-0f7b-48a8-b0d \
     --display-name="Disposable demo services (no roles, deliberately)"
   ```

2. Repoint both services at it (in-place update; no image rebuild needed):

   ```bash
   for svc in jobtrack-web enrolment-web; do
     gcloud run services update "$svc" \
       --project=project-e2ce9938-0f7b-48a8-b0d --region=europe-west1 \
       --service-account=demo-run@project-e2ce9938-0f7b-48a8-b0d.iam.gserviceaccount.com
   done
   ```

   Neither demo reads a secret, a bucket, or Cloud SQL; a no-role identity changes nothing they do.
   The SQLite demo's database is baked into its image and `enrolment-web` is self-contained. If
   either revision fails to start under the new identity, that is a finding in itself — investigate
   before proceeding, don't grant roles to make it pass.

3. Make the fix durable in the deploy scripts, so a redeploy cannot silently revert it:
   - `scripts/deploy-cloudrun.sh` (JobTrack demo): ensure the `demo-run` SA exists (create if
     absent, same pattern the PostgreSQL script uses for its three SAs) and pass
     `--service-account` on the `gcloud run deploy` line, with a comment naming this plan's §2.1.
   - `../EnrolmentRules/scripts/deploy-cloudrun.sh`: same change. **Separate commit in that
     project's tree** — JobTrack commits stay under `JobTrack/` per the monorepo rule.

### 3.2 Strip `roles/cloudbuild.builds.builder` from the default compute service account — closes §2.1's standing grant

After 3.1 (order matters — while a demo still *runs as* this SA, the role is its only unusual
privilege, but the SA itself keeps metadata-server exposure):

```bash
gcloud projects remove-iam-policy-binding project-e2ce9938-0f7b-48a8-b0d \
  --member="serviceAccount:716005672573-compute@developer.gserviceaccount.com" \
  --role="roles/cloudbuild.builds.builder"
```

The Cloud Build service agent and `716005672573@cloudbuild.gserviceaccount.com` keep their own
bindings — this removes only the default compute SA's copy. Nothing in either repository invokes
Cloud Build (both deploy paths build locally with buildx and push directly), so the expected fallout
is none; the verification stage confirms it. Defence in depth on top of 3.1: even if a future
deployment reverts to the default SA, the SA no longer carries cross-project-resource permissions.

### 3.3 Apply the `run.allowedBinaryAuthorizationPolicies` constraint — narrows §2.2, does not close it

**Live-tested outcome (2026-08-06):** applying this constraint, then attempting a fresh
`gcloud run deploy` with an unattested public image and no `--binary-authorization` flag, still
succeeded — see §2.2's rewritten evidence. The constraint restricts the *value space* of
`--binary-authorization`, not whether a deploy uses it at all. There is no Cloud Run org-policy
control that makes attestation mandatory project-wide; that only happens per-deploy, by each
script's own choice to pass `--binary-authorization=default`. Apply the constraint anyway — it is
still the documented, intended-by-design guard against a deployer explicitly requesting a *weaker*
named policy, and costs nothing once the demos are relocated (§3.4) — but do not represent §2.2 as
closed by it.

1. Self-grant the missing role at the organization (the account already holds
   `resourcemanager.organizationAdmin`, which may grant it):

   ```bash
   gcloud organizations add-iam-policy-binding 453382479060 \
     --member="user:<REDACTED_EMAIL>" --role="roles/orgpolicy.policyAdmin"
   ```

2. Apply the constraint, project-scoped (not org-wide — an org-wide constraint would also touch
   whatever project the relocated demos land in):

   ```bash
   gcloud resource-manager org-policies allow \
     run.allowedBinaryAuthorizationPolicies default \
     --project=project-e2ce9938-0f7b-48a8-b0d
   ```

3. Remove the self-granted `orgpolicy.policyAdmin` binding afterwards if standing least privilege
   at the org is preferred; the constraint persists without the role.

4. **What actually keeps `jobtrack-web-pg` attested-only** is that its own deploy script always
   passes `--binary-authorization=default`. That is the real control; document it as such in
   `postgresql-cloud-run-deployment.md` rather than crediting the org policy. §2.2's residual —
   nothing stops a *future* deploy to this project (by this script or a new one) from omitting the
   flag — is accepted risk, not remediated risk, and belongs in the gap list either way.

### 3.4 (Optional, severable) Relocate the demos to their own disposable project — closes §2.3

**Executed 2026-08-06** as `jobtrack-demo-projects` (organization `<ORG_NAME>`; `demos`/
`demo-projects` were rejected — GCP project IDs must be 6-30 characters and reject some bare words
including `project`). Deviations from the original steps, both load-bearing for anyone repeating
this:

- **A fresh project's default compute service account holds no roles at all** (Google changed this
  default; it no longer auto-grants `Editor`). `deploy-cloudrun.sh`'s plain `gcloud run deploy
  --image=...` path is unaffected (it authenticates via `gcloud auth configure-docker` and pushes
  directly), but EnrolmentRules' `gcloud run deploy --source .` path submits a build to Cloud Build,
  which needs the default compute SA to read the uploaded source from a Cloud-Build-owned bucket.
  Fix applied: `gcloud projects add-iam-policy-binding jobtrack-demo-projects
  --member=serviceAccount:<PROJECT_NUMBER>-compute@developer.gserviceaccount.com
  --role=roles/cloudbuild.builds.builder`. Granting this role here does not reopen §2.1: this
  project holds no persistent deployment, no Cloud SQL instance, no key-ring bucket, and no secret
  worth reaching — there is nothing behind this SA for a demo compromise to escalate into.
- **`cloud-run-source-deploy` must be created by hand** in the new project before the JobTrack
  demo's push — `deploy-cloudrun-postgresql.sh` creates it on demand, but the plain
  `deploy-cloudrun.sh` assumes it already exists (`gcloud artifacts repositories create
  cloud-run-source-deploy --location=europe-west1 --repository-format=docker`).
- **The old project's images were left in place — could not be deleted.** Both
  `jobtrack-web` and `enrolment-web` packages in the old project's `cloud-run-source-deploy`
  repository refused deletion (`gcloud artifacts packages delete` / `docker images delete
  --delete-tags`) with "the repository has enabled tag immutability." That immutability is a
  deliberate supply-chain control for `jobtrack-web-pg`/`jobtrack-provision`'s own images
  (`postgresql-cloud-run-deployment.md` step 1), and disabling it repository-wide just to remove two
  dead demo packages would weaken that control for no gain — the dead images are unreachable (no
  service references them) and carry no elevated permission. Accepted as a residual: the old
  project's Artifact Registry repository permanently retains a handful of superseded demo-image
  layers.
- The now-unused `demo-run@project-e2ce9938-0f7b-48a8-b0d.iam.gserviceaccount.com` **was** deleted
  successfully (service-account deletion is unaffected by tag immutability).
- §3.3 was then applied to the JobTrack project as planned — see §2.2/§3.3 for why this narrows
  rather than closes that finding, unaffected by the relocation.

End state: the JobTrack project contains exactly one service (`jobtrack-web-pg`), three purpose-made
SAs, and no identity that is not part of the persistent deployment's own design.

### 3.5 Documentation and index reconciliation

- `docs/operations/postgresql-cloud-run-deployment.md`: update §"What still separates this from a
  production deployment" — replace the org-policy bullet with an accurate one: the
  `run.allowedBinaryAuthorizationPolicies` constraint is applied, but (per §2.2/§3.3's live test)
  it does not make attestation mandatory; what actually keeps this service attested-only is that its
  own deploy script always passes `--binary-authorization=default`, which is script discipline, not
  policy enforcement. Add a line under "Identity and least privilege" stating the co-tenancy rule
  this plan establishes: *no other workload in the project may run as an identity holding any role
  on the key-ring bucket, the secrets, or the Cloud SQL instance — the default compute SA is
  repointed away and stripped, not trusted*.
- `docs/operations/docker-image.md`: note the demo's relocation to `jobtrack-demo-projects`, its
  dedicated no-role SA, and the new URLs.
- EnrolmentRules' own docs: same relocation note for `enrolment-web`, in that repository.
- `docs/plans/README.md`: add this plan's row; keep the status in this file authoritative.

## 4. Verification

Re-run the audit that produced the findings; results below are from the live run on 2026-08-06.

| Check | Expect | Result |
|---|---|---|
| `gcloud run services describe jobtrack-web`/`enrolment-web` in `jobtrack-demo-projects` | `demo-run@jobtrack-demo-projects…`, HTTP 200/302 | **Pass** — both confirmed |
| `gcloud run services list --project=project-e2ce9938-0f7b-48a8-b0d` | only `jobtrack-web-pg` | **Pass** |
| `gcloud projects get-iam-policy project-e2ce9938-0f7b-48a8-b0d --flatten=bindings --filter='bindings.members:716005672573-compute@'` | no `cloudbuild.builds.builder` row | **Pass** |
| `gcloud resource-manager org-policies list --project=project-e2ce9938-0f7b-48a8-b0d` | `run.allowedBinaryAuthorizationPolicies` listed | **Pass** |
| Negative test: unattested public image, no `--binary-authorization` flag, deployed to `project-e2ce9938-0f7b-48a8-b0d` | rejected | **Fail — deployed successfully.** This is the finding rewritten into §2.2: the constraint does not enforce mandatory attestation. Test service deleted after confirming the result. |
| `demo-run@project-e2ce9938-0f7b-48a8-b0d.iam.gserviceaccount.com` | deleted | **Pass** |
| Persistent-instance regression: `jobtrack-web-pg` still serving on `jobtrack-run` SA throughout | key ring untouched | **Pass** — never modified during this work |

The negative test is the one that mattered, and it disproved the plan's original assumption about
what the org policy does. Treat any future claim that "Binary Authorization is enforced project-wide"
in this project as false until GCP ships an actual mandatory-attestation control for Cloud Run — the
current guarantee is per-service and script-enforced only.

## 5. Residual risks after this plan

- **In-project residuals already documented** in `postgresql-cloud-run-deployment.md`'s gap list
  (blanket `ForwardedHeaders` trust — correct on Cloud Run; the Cloud SQL admin password existing
  in Secret Manager; single instance; no alerting) are unchanged by this plan.
- **§2.2 is not closed, only narrowed.** No Cloud Run org policy makes Binary Authorization
  mandatory; `jobtrack-web-pg` stays attested-only because its deploy script always sets
  `--binary-authorization=default`. Any new deploy path added to this project without that flag
  bypasses attestation silently — this is a standing review item, not a solved problem.
- **The old project's Artifact Registry repository retains dead demo-image layers** (§3.4) because
  removing them would require disabling tag immutability, which protects the live images instead.
  They are unreachable and carry no permission, so this is inert, not a live risk.
- **A fresh GCP project's default compute SA gets no roles by default** (a Google platform change
  since this deployment's original docs were written) — `deploy-cloudrun-postgresql.sh`'s own
  three-SA design is unaffected (it never relies on the default compute SA), but any *new*
  source-based (`--source .`) deploy path added to any project in this org will hit the same
  `storage.objects.get` failure §3.4 worked around, and needs the same fix applied deliberately
  rather than by reflexively granting broad roles.
- The project owner is a single user account; org-level IAM hygiene (2FA on the Google account,
  no standing `orgpolicy.policyAdmin`) is outside this plan's scope but is the root that everything
  above hangs from.
