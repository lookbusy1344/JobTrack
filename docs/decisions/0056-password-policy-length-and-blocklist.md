# ADR 0056: Length-only password policy, local blocklist, no forced re-validation of existing accounts

**Status:** Accepted
**Date:** 2026-08-01
**Closes:** Security audit remediation plan §2.1.

## Context

`PasswordPolicy.MinimumLength` was 6, and every credential-setting route additionally required at
least one letter and one digit. A password such as `aaaaa1` satisfied it. MFA is optional, so every
account must be treated as password-only when the password is established. Current
[NIST SP 800-63B](https://pages.nist.gov/800-63-4/sp800-63b.html#passwordver) password-verifier
guidance requires at least 15 characters for a single-factor password, no character-class
composition rule, acceptance of at least 64 characters, and comparison against a blocklist of
common or compromised values.

Auditing the actual enforcement also found the policy was inconsistently applied: only self-service
password change (`AccountCredentialCommands.ChangeOwnPasswordAsync`) called it. Employee creation,
employee password reset, and administrator bootstrap hashed the supplied password directly with no
policy check at all — `Microsoft.AspNetCore.Identity`'s own `UserManager.CreateAsync`/
`ChangePasswordAsync` (and therefore its configured `IdentityOptions.Password`/
`IPasswordValidator<T>` pipeline) are never called anywhere in this codebase; every real
credential-setting path goes through the reusable library's own commands instead.

## Decision

- **Length only, no composition rule.** `PasswordPolicy.MinimumLength` is now 15 Unicode code points
  (counted via `Rune` enumeration, not UTF-16 code units, so a password containing surrogate-pair
  characters isn't double-counted), `PasswordPolicy.MaximumLength` is 128. No letter/digit/case/symbol
  requirement. `RequiresLetterPasswordValidator` is deleted along with its test — it enforced exactly
  the composition rule this ADR removes, and (per the point above) was already unreachable from any
  real credential-setting path.
- **A local, deterministic blocklist.** `PasswordBlocklist.Contains` rejects the product name
  (`JobTrack`, any case), the account's own username, and a small fixed set of well-known
  breached/common passwords 15+ characters long (including `correcthorsebatterystaple`, blocklisted
  precisely because its fame from XKCD 936 makes it a common guess). Comparison is exact and
  case-insensitive — never a substring match, which would over-block ordinary long passphrases that
  happen to contain a common word. No password is ever sent to a third party to perform this check.
- **One shared enforcement point.** `PasswordPolicyGuard.EnsureAcceptable(password, username)` (new,
  `JobTrack.Application`, internal) is called by every command that sets a new credential:
  `AccountCredentialCommands.ChangeOwnPasswordAsync`, `EmployeeCommands.CreateEmployeeAsync`,
  `EmployeeCommands.ResetPasswordAsync`, and `InstallationCommands.BootstrapAdministratorAsync`. All
  four raise the same `InvariantViolationException` `ConstraintId`,
  `"account-new-password-policy"`, so callers keep one error-handling path.
  `ChangeOwnPasswordRequest.Username` and `ResetEmployeePasswordRequest.TargetUserName` are new
  required members carrying the username the blocklist check needs; `CreateEmployeeRequest.UserName`
  and `BootstrapAdministratorRequest.UserName` already existed.
- **Existing accounts are not forced through a password change.** A stored hash cannot reveal whether
  the password behind it satisfies the new policy — inspecting the database can't answer this, so
  there is no way to *selectively* force only the accounts that need it. Forcing every existing
  account through a change on next login was considered and rejected: it is a disproportionate,
  all-or-nothing UX disruption for a length/composition tightening, not a response to a suspected
  compromise. The new policy applies to every password set *from this point forward* (new accounts,
  administrator resets, self-service changes); existing accounts keep their current password until
  they change it through one of those paths themselves.
- **The published Docker demo image keeps fixed credentials, but they satisfy the policy.** The
  staff and requester demo passwords were lengthened while remaining published/reusable. There is
  no `CreateEmployeeRequest` or Admin CLI bypass: every employee-creation call enforces the same
  verifier.

## Consequences

- Every new/reset/changed password must be 15–128 Unicode code points and not blocklisted; composition
  rules are gone.
- `RequiresLetterPasswordValidator` and its test are deleted; `IdentityOptions.Password.RequiredLength`
  still mirrors `PasswordPolicy.MinimumLength` for documentation/defense-in-depth even though nothing
  currently calls `UserManager.CreateAsync`/`ChangePasswordAsync`.
- The production library and CLI expose no weak-password carve-out.
- Existing accounts are unaffected until their own next password-setting action; no forced-reset
  rollout was implemented or is planned as part of this ADR.
