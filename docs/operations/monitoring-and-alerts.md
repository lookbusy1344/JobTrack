# Monitoring and alerts for the Cloud Run + Cloud SQL deployment

This runbook covers the minimal production alert baseline for `jobtrack-web-pg` and `jobtrack-pg`.
Full distributed tracing remains separately scoped; this baseline is deliberately small enough to be
actionable and mandatory on every deployment.

`scripts/configure-cloudrun-monitoring.sh` owns the configuration and is called by
`scripts/deploy-cloudrun-postgresql.sh`. It reconciles policies by their unique display names and
labels them `managed_by=jobtrack_deploy`. Do not create a second policy with one of those names.

## Notification delivery

Set `JOBTRACK_MONITORING_NOTIFICATION_CHANNEL` to the full resource name of an enabled Cloud
Monitoring notification channel before deploying:

```bash
export JOBTRACK_MONITORING_NOTIFICATION_CHANNEL=projects/<project>/notificationChannels/<id>
./scripts/deploy-cloudrun-postgresql.sh <project> europe-west1
```

List existing channels before creating a new one — most projects already have one:

```bash
gcloud alpha monitoring channels list --project <project> \
  --format="table(name,type,displayName,labels.email_address)"
```

The configuration script validates the channel through the Monitoring API and fails before reading
deployment secrets if it is absent, disabled, or explicitly `UNVERIFIED`. Google treats a
`VERIFICATION_STATUS_UNSPECIFIED` channel as operational when its type does not require verification
or the channel predates that requirement; the script follows that API contract. A policy without a
delivery destination is not an operational alert. Use a managed team/on-call destination rather than
an individual's personal mailbox; test delivery after creating or changing it.

## Policies

| Policy | Threshold | First response |
| --- | --- | --- |
| Cloud Run sustained 5xx | More than 0.05 container-served 5xx responses/second for five minutes (15 in the window) | Check current revision errors, `/health/ready`, recent deploys, and Cloud SQL state. Roll traffic back only to a schema-compatible retained revision. |
| Cloud Run p95 latency | Over 2,000 ms for five minutes | Check database connection pressure, pool waits, slow-query evidence, and instance count before changing capacity. |
| Cloud Run restart loop | More than ten instance-start log entries in five minutes | Distinguish expected scale-out from startup failure or process churn; inspect revision system and application logs. |
| Cloud SQL disk saturation | Over 80% for ten minutes | Confirm storage auto-resize is still enabled and investigate abnormal table, index, WAL, or temporary-file growth. |
| Cloud SQL connection saturation | More than 80 of the verified 100 PostgreSQL backends for five minutes | Identify which application pool or operator workload consumed the reserve. Do not increase a pool independently of ADR 0066's aggregate budget. |
| Cloud SQL automated backup failure | Any automated attempt whose `windowStatus` is not `STATUS_SUCCEEDED` | Inspect the backup run immediately, preserve the last successful recovery point, and initiate a manual backup once the cause is resolved. |

The restart and backup policies use the user-defined log metrics
`jobtrack_cloud_run_instance_starts` and `jobtrack_cloud_sql_backup_failures`. The other four use
Google's built-in Cloud Run and Cloud SQL metrics.

## Audit logging

Every deployment enables targeted Data Access logs for:

- Secret Manager `ADMIN_READ` and `DATA_READ`, including `AccessSecretVersion`;
- IAM `ADMIN_READ` and `DATA_READ`, which also enables IAM Service Account Credentials audit logs
  because Google does not permit configuring `iamcredentials.googleapis.com` independently; and
- Cloud KMS administrative reads and cryptographic operations.

Admin Activity logs remain always-on. Data Access logs live in the project's `_Default` bucket and
therefore follow its configured retention and access controls. Review secret reads by principal,
secret resource and deployment window; the runtime should read only its four connection strings and
two certificate secrets, while the provisioning identity's access is temporary and condition-bound.

## Routine verification

After every deploy:

1. Confirm all six managed policies are enabled and reference the intended notification channel.
2. Confirm both user log metrics exist.
3. Confirm the project IAM policy retains the three targeted `auditConfigs` entries with no exempted
   principals.
4. Send a notification-channel test through Cloud Monitoring after any channel change.
5. Check the latest automated backup is successful and within the expected daily window.

Quarterly, review thresholds against observed traffic and capacity. A threshold change is an
operational decision recorded here and in the script; never tune an alert merely to silence an
uninvestigated incident.
