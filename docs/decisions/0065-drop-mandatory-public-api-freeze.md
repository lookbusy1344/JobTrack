# ADR 0065: Drop the mandatory public-API compatibility freeze

**Status:** Accepted
**Supersedes:** ADR 0013's binding compatibility-commitment/breaking-change-review requirement.

## Context

ADR 0013 treated `JobTrack.Abstractions`, `JobTrack.Domain`, `JobTrack.Application`, and both
persistence providers' public surface as a **compatibility commitment** once the library gate
(§7.5) passed, with a formal breaking-change process (PR note, same-change consumer updates,
reviewed baseline update). The 2026-08-07 FDG follow-up plan carried this out mechanically, moving
the accepted surface from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`.

In practice `JobTrack.Web` and `JobTrack.AdminCli` are the library's only consumers, both built and
released from this same repository (ADR 0013 §"Decision" already noted this). There is no external
package consumer to protect, and no published NuGet cadence to synchronize with. Treating the
surface as frozen adds review ceremony (breaking-change notes, baseline diffs staged as their own
reviewed step) without a corresponding consumer who needs the stability guarantee — any break is
fixed in the same change as its only caller, in the same PR, in the same repository.

## Decision

The public API of `Abstractions`/`Domain`/`Application`/both providers is **no longer a mandatory
compatibility commitment**. It changes as needed, same as any other in-repo surface.

Keeping public members well-designed, minimal, and reviewed against
`Framework_Design_Guidelines_Essentials.md` remains **good practice** — it's cheap and pays off if
the library is ever extracted for external consumption — but it is not a gate, not a required review
step, and not something to hold up a change for.

## Consequences

- `PublicApi.Tests`' approved-surface baseline may be regenerated freely as the surface changes; no
  separate reviewed "accept the diff" step is required.
- The distinction between `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` is no longer
  meaningful as a freeze boundary; either file may be updated without the promotion ceremony ADR
  0013 described.
- If the library is ever extracted for external/multi-repo consumption, this ADR is revisited and
  real compatibility discipline (versioning, deprecation windows, a reviewed breaking-change
  process) is reinstated for that surface — out of scope while the only consumers ship from this
  repository.
- ADR 0013 is superseded; its historical record of why the commitment was originally adopted stays
  as written for context.
