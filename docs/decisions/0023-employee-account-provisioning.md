# ADR 0023: Administrators provision new employee accounts through `IEmployeeCommands`

**Status:** Accepted
**Closes:** Implementation plan §8.5 slice 10 ("administrator account provisioning, disablement,
reset, and revocation")

## Decision

`IEmployeeCommands` gains `CreateEmployeeAsync`, callable only by an actor holding
`EmployeeRole.Administrator` (the same `EmployeeAccessPolicy.CanManageAccounts` gate already used
by `SetEnabledAsync`/`ResetPasswordAsync`). It creates one `app_user` row, one `identity_user` row,
and grants exactly one initial `EmployeeRole` in a single transaction, mirroring
`IInstallationCommands.BootstrapAdministratorAsync`'s app_user-then-identity_user-then-role
sequencing (ADR 0005) but without the root-job-node creation or the "only if uninitialised" guard —
this command is the ordinary, repeatable path for every account after the first.

The created account starts enabled, with `requires_password_change = true` (matching
`ResetPasswordAsync`'s behaviour — a caller-set initial credential is provisional, never a password
the new employee is expected to keep). The caller supplies exactly one initial `EmployeeRole`
(including `Administrator` itself, letting an administrator provision a second administrator) rather
than zero or many: a freshly created account needs at least one role to be useful immediately, and
`AssignRoleAsync`/`RevokeRoleAsync` already exist for every subsequent role change, so this command
does not duplicate that facility.

A duplicate `user_name` (case-insensitively, via the existing `identity_user.normalized_user_name`
unique constraint from schema version 0002) is rejected as `InvariantViolationException`
(`ConstraintId` `"employee-username-already-taken"`), translated from the underlying unique-
constraint violation the same way `JobTrack.Persistence.Shared.JobNodeWriteExceptionTranslation`
translates job-node write conflicts — relying on the database constraint itself for atomicity under
concurrent creation attempts, rather than an application-level pre-check-then-insert race.

## Why this was undecided

Spec §7.2 states only "accounts shall be created, enabled, disabled, and assigned permissions by an
administrator" without naming a mechanism, and unlike bootstrap (ADR 0005) or emergency reset
(impl plan §8.6), no ADR or CLI command previously covered ongoing account creation — every
integration test seeded subsequent employees directly against `app_user`/`identity_user` with raw
SQL. Per this repository's decision-precedence convention, that gap is surfaced here rather than
silently assumed to be CLI-only or web-only.

## Consequences

- `JobTrack.Web`'s Administrator-only account-management surface gains a "create employee" page
  (plan §8.5 slice 10) alongside the existing enable/disable, reset, and role grant/revoke pages.
- `JobTrack.AdminCli` gets no new command: CLI-side provisioning was never in scope for this
  decision and remains limited to bootstrap and emergency reset (impl plan §8.6).
- Self-registration remains out of scope; every account, including subsequent administrators,
  is provisioned by an existing administrator.
