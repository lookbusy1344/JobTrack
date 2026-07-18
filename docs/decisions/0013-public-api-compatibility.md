# ADR 0013: Internal public API compatibility and versioning policy

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 10

## Decision

`JobTrack.Abstractions`, `JobTrack.Domain`, `JobTrack.Application`, and both persistence providers' public surface (the `IJobTrackClient` facade and everything it exposes) are treated as **compatibility commitments once the library gate (§7.5) passes**, even though the library is an internal monorepo component rather than a published external NuGet package (§7.5 explicitly notes this). The distinction matters because `JobTrack.Web` and `JobTrack.AdminCli` are the only consumers, both built and released from the same repository — this permits a lighter versioning scheme than a public package would need, while still requiring the same design discipline up front.

**Versioning scheme.** Semantic-versioning-shaped, but enforced as an in-repo compatibility baseline rather than published package versions:

- **Additive evolution after 1.0** (plan §7.1): new members, new overloads, new optional-by-default behaviour may be added without a breaking-change review.
- **Breaking changes** (removing/renaming a public member, changing a method's observable contract, narrowing a parameter type, widening an exception contract) require: (a) an explicit note in the PR description identifying the break, (b) updating every in-repo consumer in the same change (there is no external consumer to stage a deprecation window for), and (c) a corresponding update to the API-approval baseline (below) reviewed as part of that PR, not silently regenerated.
- There is no public NuGet release cadence to synchronize with; "1.0" here means "library gate accepted" (§7.5, M6), not a published package version number.

**Enforcement mechanism.** API-approval tests (e.g. a `PublicApi.Tests`-recorded surface snapshot, per the existing `JobTrack.PublicApi.Tests` project) fail the build on any unreviewed public-surface change, additive or breaking — the reviewer explicitly accepts the diff to the recorded baseline as part of every PR that touches a public type. This is the mechanism referenced by plan §7.1's "API compatibility baselines checked in CI."

## Consequences

- `JobTrack.PublicApi.Tests` owns the approved-surface baseline file(s); regenerating the baseline without review is treated the same as silently accepting an unreviewed breaking change and is disallowed by convention (enforced by code review, not tooling, since the repo has no external gatekeeper).
- **Pre-1.0 the entire surface lives in `PublicAPI.Unshipped.txt`; `PublicAPI.Shipped.txt` holds only `#nullable enable`.** This is the intended `Microsoft.CodeAnalysis.PublicApiAnalyzers` convention, not an incomplete baseline: every public member is still recorded and reviewed (the analyzer fails the build on any unrecorded surface change, additive or breaking, in either file), but nothing has been *frozen* as shipped because "1.0" here means "library gate accepted" (§7.5, M6) and that gate has not yet closed. The Unshipped→Shipped promotion happens **once**, at the library gate: the reviewed Unshipped surface is moved into `PublicAPI.Shipped.txt` in that acceptance change, after which additive members land in Unshipped and removals require the breaking-change process above. An empty-looking `Shipped.txt` before that point is correct and must not be "fixed" by prematurely promoting the surface.
- Architecture tests (§7.5) enforce that no provider, SQL, ASP.NET Core, or mutable entity type leaks into the surface the baseline covers.
- If the library is ever extracted for external/multi-repo consumption, this ADR is revisited to add real package versioning (a version number consumers pin to) — that is out of scope for the initial release and not designed for speculatively now.
